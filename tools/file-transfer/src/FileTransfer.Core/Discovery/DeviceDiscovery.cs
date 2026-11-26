using System.Diagnostics;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// One bounded device discovery window, as required by
/// docs/mobile-ux/FILE_ASSISTANT_CONTRACT.md for <c>file-transfer.assistant.devices</c>.
/// <para>
/// Call it once when the device panel opens; it never polls and never runs in the background. It
/// gathers candidates from the known-device list, the Tailscale peer list and the MPT LAN protocol,
/// deduplicates them, and then asks each candidate for its protocol v3 identity with a bounded number
/// of simultaneous probes. Only a candidate that really answered becomes
/// <see cref="DiscoveryDeviceResult.Available"/>; an old receiver, a refused connection or an
/// expired window leaves the device unavailable with a reason.
/// </para>
/// <para>
/// Cancelling <c>token</c> cancels the whole window and throws <see cref="OperationCanceledException"/>.
/// An expired internal window is not an error: it returns a partial
/// <see cref="DiscoveryOutcome.WindowExpired"/> report of everything that finished.
/// </para>
/// </summary>
public sealed class DeviceDiscovery
{
    private readonly DiscoveryOptions _options;
    private readonly IReadOnlyList<IDiscoveryCandidateSource> _sources;
    private readonly IIdentityProbe _probe;

    /// <param name="sources">Replaces the default sources; tests inject fake peer sources here.</param>
    /// <param name="probe">Replaces the real v3 hello client; tests inject a fake probe here.</param>
    /// <param name="lanChannel">
    /// An already running <see cref="LanDiscoveryChannel"/> (the one a <see cref="DiscoveryBeacon"/>
    /// owns) so an active discovery reuses that socket instead of binding a second one.
    /// </param>
    public DeviceDiscovery(
        DiscoveryOptions? options = null,
        IEnumerable<IDiscoveryCandidateSource>? sources = null,
        IIdentityProbe? probe = null,
        LanDiscoveryChannel? lanChannel = null)
    {
        _options = options ?? DiscoveryOptions.Default;
        _options.Validate();
        _probe = probe ?? new HelloIdentityProbe(_options);
        _sources = sources?.ToArray() ?? DefaultSources(_options, lanChannel);
    }

    public DiscoveryOptions Options => _options;

    private static IReadOnlyList<IDiscoveryCandidateSource> DefaultSources(DiscoveryOptions options, LanDiscoveryChannel? channel)
    {
        var sources = new List<IDiscoveryCandidateSource>();
        if (options.TailscaleEnabled) sources.Add(new TailscalePeerSource());
        if (options.LanEnabled) sources.Add(new LanCandidateSource(channel));
        return sources;
    }

    public async Task<DiscoveryReport> DiscoverAsync(IReadOnlyList<DiscoveredDevice> known, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(known);
        var started = Stopwatch.GetTimestamp();
        var deadline = DateTimeOffset.UtcNow + _options.Window;
        using var window = new CancellationTokenSource(_options.Window);
        var diagnostics = new List<string>();

        var candidates = new List<DiscoveryCandidate>();
        foreach (var device in known)
        {
            if (device is null) continue;
            if (_options.SelfDeviceId.Length > 0 && device.DeviceId == _options.SelfDeviceId) continue;
            candidates.Add(new DiscoveryCandidate(device, true, DiscoverySource.Known) { Rank = 0, Hint = "已记录设备" });
        }

        // Sources run together and each one is bounded by the collection budget, so one slow source
        // (a hung CLI, a network without multicast answers) can never consume the whole window.
        var request = new DiscoveryRequest(known, _options);
        var budget = CollectBudget();
        var collected = await Task.WhenAll(_sources.Select(source => CollectAsync(source, request, budget, token)));
        token.ThrowIfCancellationRequested();

        var supported = 0;
        foreach (var result in collected)
        {
            if (result.Supported) supported++;
            diagnostics.AddRange(result.Diagnostics);
            candidates.AddRange(result.Candidates);
        }
        if (_sources.Count == 0) diagnostics.Add("没有启用的发现来源。");

        var ordered = Dedupe(candidates, diagnostics);
        var probeable = new List<DiscoveryCandidate>();
        var blocked = new List<DiscoveryCandidate>();
        foreach (var candidate in ordered)
        {
            if (DiscoveryAddresses.IsUsableCandidate(candidate.Device.Address) && DiscoveryAddresses.IsValidPort(candidate.Device.Port))
                probeable.Add(candidate);
            else blocked.Add(candidate);
        }
        if (blocked.Count > 0)
            diagnostics.Add($"{blocked.Count} 个候选的地址或端口不是 Tailnet 地址，已跳过身份查询。");

        var toProbe = probeable.Take(_options.MaxCandidates).ToList();
        var capped = probeable.Skip(_options.MaxCandidates).ToList();
        if (capped.Count > 0)
            diagnostics.Add($"候选超过本次上限 {_options.MaxCandidates} 个，其余 {capped.Count} 个未做身份查询。");

        var expired = new ExpiryFlag();
        var outcomes = await ProbeAsync(toProbe, deadline, expired, token);
        token.ThrowIfCancellationRequested();

        var devices = Assemble(outcomes, blocked, capped, request, diagnostics);
        var outcome = supported == 0 && known.Count == 0
            ? DiscoveryOutcome.Unsupported
            : expired.Value || window.IsCancellationRequested ? DiscoveryOutcome.WindowExpired : DiscoveryOutcome.Completed;
        if (outcome == DiscoveryOutcome.Unsupported && diagnostics.Count == 0)
            diagnostics.Add("当前平台没有可用的设备发现来源。");

        return new DiscoveryReport(devices, outcome, diagnostics, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Collection gets the window minus one probe budget, so probing always has time left.</summary>
    private TimeSpan CollectBudget()
    {
        var budget = _options.Window - _options.ProbeTimeout;
        if (budget < TimeSpan.FromMilliseconds(250)) budget = TimeSpan.FromMilliseconds(250);
        return budget > _options.Window ? _options.Window : budget;
    }

    private async Task<CandidateSourceResult> CollectAsync(
        IDiscoveryCandidateSource source, DiscoveryRequest request, TimeSpan budget, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(budget);
        try
        {
            return await source.CollectAsync(request, bound.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new CandidateSourceResult([], [$"{source.Name}：没有在本次发现窗口内完成。"] , true);
        }
        catch (Exception ex)
        {
            return new CandidateSourceResult([], [$"{source.Name}读取失败：{ex.Message}"], false);
        }
    }

    /// <summary>Deduplicates by address and port, promotes candidates that turn out to be known devices, and caps the queue.</summary>
    private List<DiscoveryCandidate> Dedupe(List<DiscoveryCandidate> candidates, List<string> diagnostics)
    {
        var pairedIds = candidates
            .Where(candidate => candidate.Paired && candidate.Device.DeviceId.Length > 0)
            .Select(candidate => candidate.Device.DeviceId)
            .ToHashSet(StringComparer.Ordinal);
        var byAddress = new Dictionary<string, DiscoveryCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (_options.SelfDeviceId.Length > 0 && candidate.Device.DeviceId == _options.SelfDeviceId) continue;
            var address = DiscoveryAddresses.Normalize(candidate.Device.Address);
            if (address.Length == 0)
            {
                diagnostics.Add($"已忽略没有地址的候选（{candidate.Device.Name}）。");
                continue;
            }
            var key = address + ":" + candidate.Device.Port;
            byAddress[key] = byAddress.TryGetValue(key, out var existing) ? Merge(existing, candidate) : candidate;
        }

        var merged = new List<DiscoveryCandidate>(byAddress.Count);
        foreach (var candidate in byAddress.Values)
        {
            var paired = candidate.Paired
                || (candidate.Device.DeviceId.Length > 0 && pairedIds.Contains(candidate.Device.DeviceId));
            merged.Add(paired == candidate.Paired ? candidate : candidate with { Paired = paired });
        }
        var duplicates = candidates.Count - merged.Count;
        if (duplicates > 0) diagnostics.Add($"已合并 {duplicates} 个重复候选。");

        return merged
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Device.Address, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Keeps the strongest facts of two candidates that describe the same endpoint.</summary>
    private static DiscoveryCandidate Merge(DiscoveryCandidate first, DiscoveryCandidate second)
    {
        var primary = first.Rank <= second.Rank ? first : second;
        var other = ReferenceEquals(primary, first) ? second : first;
        var device = primary.Device with
        {
            DeviceId = primary.Device.DeviceId.Length > 0 ? primary.Device.DeviceId : other.Device.DeviceId,
            Name = primary.Device.Name.Length > 0 ? primary.Device.Name : other.Device.Name,
            Platform = primary.Device.Platform.Length > 0 ? primary.Device.Platform : other.Device.Platform,
        };
        return primary with { Device = device, Paired = first.Paired || second.Paired };
    }

    private async Task<List<ProbeOutcome>> ProbeAsync(
        List<DiscoveryCandidate> candidates, DateTimeOffset deadline, ExpiryFlag expired, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(_options.MaxConcurrency);
        var tasks = candidates.Select(async candidate =>
        {
            await gate.WaitAsync(token);
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    expired.Value = true;
                    return new ProbeOutcome(candidate, new IdentityProbeResult(IdentityProbeStatus.Unreachable,
                        candidate.Device, "发现窗口已结束，未完成身份查询。"));
                }
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
                bound.CancelAfter(remaining);
                try
                {
                    var result = await _probe.ProbeAsync(candidate, bound.Token);
                    if (result.Status == IdentityProbeStatus.Unreachable && DateTimeOffset.UtcNow >= deadline) expired.Value = true;
                    return new ProbeOutcome(candidate, result);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    expired.Value = true;
                    return new ProbeOutcome(candidate, new IdentityProbeResult(IdentityProbeStatus.Unreachable,
                        candidate.Device, "发现窗口已结束，未完成身份查询。"));
                }
            }
            finally { gate.Release(); }
        }).ToArray();
        var outcomes = await Task.WhenAll(tasks);
        return outcomes.ToList();
    }

    private static List<DiscoveryDeviceResult> Assemble(
        List<ProbeOutcome> outcomes,
        List<DiscoveryCandidate> blocked,
        List<DiscoveryCandidate> capped,
        DiscoveryRequest request,
        List<string> diagnostics)
    {
        var output = new List<DiscoveryDeviceResult>();
        var byDeviceId = new Dictionary<string, int>(StringComparer.Ordinal);
        var dropped = 0;
        foreach (var outcome in outcomes)
        {
            var result = outcome.Result;
            if (!result.Available)
            {
                // An unpaired candidate that did not answer is not a device: it never enters the list,
                // so a tailnet full of machines that do not run MPT is not presented as "discovered".
                if (outcome.Candidate.Paired) output.Add(new DiscoveryDeviceResult(outcome.Candidate.Device, true, false, outcome.Candidate.Source, result.Message));
                else dropped++;
                continue;
            }
            var paired = outcome.Candidate.Paired || request.IsKnownDeviceId(result.Device.DeviceId);
            var entry = new DiscoveryDeviceResult(result.Device, paired, true, outcome.Candidate.Source, result.Message);
            if (byDeviceId.TryGetValue(result.Device.DeviceId, out var index))
            {
                // One device can answer on more than one address; keep the paired record, else the first.
                if (!output[index].Paired && entry.Paired) output[index] = entry;
                continue;
            }
            byDeviceId[result.Device.DeviceId] = output.Count;
            output.Add(entry);
        }
        foreach (var candidate in blocked.Concat(capped))
        {
            if (!candidate.Paired) { dropped++; continue; }
            var message = blocked.Contains(candidate)
                ? "地址或端口不是有效的 Tailnet 地址，已跳过身份查询。"
                : $"超过本次发现上限 {request.Options.MaxCandidates} 个，未做身份查询。";
            output.Add(new DiscoveryDeviceResult(candidate.Device, true, false, candidate.Source, message));
        }
        if (dropped > 0)
            diagnostics.Add($"{dropped} 个候选没有通过 v3 身份查询或地址无效，未加入设备列表（旧版接收器或未运行接收服务）。");

        output.Sort((a, b) =>
        {
            var paired = b.Paired.CompareTo(a.Paired);
            if (paired != 0) return paired;
            var available = b.Available.CompareTo(a.Available);
            if (available != 0) return available;
            var name = string.Compare(a.Device.Name, b.Device.Name, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : string.CompareOrdinal(a.Device.Address, b.Device.Address);
        });
        return output;
    }

    private sealed record ProbeOutcome(DiscoveryCandidate Candidate, IdentityProbeResult Result);

    private sealed class ExpiryFlag
    {
        private volatile bool _value;
        public bool Value { get => _value; set => _value = value; }
    }
}
