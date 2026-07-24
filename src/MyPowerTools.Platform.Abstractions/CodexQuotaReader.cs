using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MyPowerTools.Platform.Abstractions;

public sealed record CodexQuotaWindow(
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public int RemainingPercent =>
        (int)Math.Round(Math.Clamp(100d - UsedPercent, 0d, 100d), MidpointRounding.ToEven);
}

public sealed record CodexQuotaSnapshot(
    CodexQuotaWindow? ShortWindow,
    CodexQuotaWindow? WeeklyWindow,
    string Source)
{
    public CodexQuotaWindow? DisplayWindow => WeeklyWindow ?? ShortWindow;
}

public static class CodexQuotaReader
{
    private const string AppServerMethod = "account/rateLimits/read";
    private const int SessionTailBytes = 4 * 1024 * 1024;
    private static string? _cachedCodexExecutable;

    public static async Task<CodexQuotaSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        Exception? appServerError = null;
        try
        {
            return await ReadFromAppServerAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            appServerError = ex;
        }

        try
        {
            return await ReadFromSessionsAsync(cancellationToken);
        }
        catch (Exception sessionError) when (sessionError is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Codex quota is unavailable. App server: {appServerError?.Message}; sessions: {sessionError.Message}",
                sessionError);
        }
    }

    internal static CodexQuotaSnapshot ParseAppServerResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryGetObject(root, "result", out var result))
        {
            throw new InvalidDataException("Codex rate-limit response has no result object.");
        }

        JsonElement rateLimits = default;
        if ((TryGetValue(result, "rateLimitsByLimitId", out var byLimitId) ||
             TryGetValue(result, "rate_limits_by_limit_id", out byLimitId)) &&
            TryFindCodexLimit(byLimitId, out var codexLimit))
        {
            rateLimits = codexLimit;
        }
        else if (TryGetObject(result, "rateLimits", out var defaultLimits) ||
                 TryGetObject(result, "rate_limits", out defaultLimits))
        {
            rateLimits = defaultLimits;
        }

        if (rateLimits.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex rate-limit response has no total quota bucket.");
        }

        return ParseRateLimits(rateLimits, "app-server");
    }

    internal static CodexQuotaSnapshot ParseSessionEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryGetObject(root, "payload", out var payload) ||
            ReadString(payload, "type") is not ("token_count" or "tokenCount") ||
            !(TryGetObject(payload, "rate_limits", out var rateLimits) ||
              TryGetObject(payload, "rateLimits", out rateLimits)))
        {
            throw new InvalidDataException("Session event has no Codex token-count rate limits.");
        }

        return ParseRateLimits(rateLimits, "sessions");
    }

    private static async Task<CodexQuotaSnapshot> ReadFromAppServerAsync(CancellationToken cancellationToken)
    {
        var executable = FindCodexExecutable()
            ?? throw new FileNotFoundException("Codex app-server executable was not found.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Codex app-server could not be started.");
        _ = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":"mpt-init","method":"initialize","params":{"clientInfo":{"name":"mypowertools-quota","title":"MyPowerTools Quota","version":"0.1.0"},"capabilities":{"experimentalApi":true,"optOutNotificationMethods":[]}}}""");
            await process.StandardInput.FlushAsync(timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            await ReadResponseLineAsync(process, "mpt-init", timeout.Token);

            await process.StandardInput.WriteLineAsync(
                $$"""{"jsonrpc":"2.0","id":"mpt-rate-limits","method":"{{AppServerMethod}}"}""");
            await process.StandardInput.FlushAsync(timeout.Token);
            var rateLimitResponse = await ReadResponseLineAsync(
                process,
                "mpt-rate-limits",
                timeout.Token);
            return ParseAppServerResponse(rateLimitResponse);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<string> ReadResponseLineAsync(
        Process process,
        string responseId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidDataException(
                    $"Codex app-server closed before returning response {responseId}.");
            }

            try
            {
                using var response = JsonDocument.Parse(line);
                if (ReadString(response.RootElement, "id") != responseId)
                {
                    continue;
                }
                if (response.RootElement.TryGetProperty("error", out var error))
                {
                    throw new InvalidDataException(
                        $"Codex app-server rejected request {responseId}: {error}");
                }
                return line;
            }
            catch (JsonException)
            {
            }
        }
    }

    private static async Task<CodexQuotaSnapshot> ReadFromSessionsAsync(CancellationToken cancellationToken)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        var sessionsRoot = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            throw new DirectoryNotFoundException($"Codex sessions directory was not found: {sessionsRoot}");
        }

        var files = Directory
            .EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(32)
            .ToArray();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadTailAsync(file.FullName, SessionTailBytes, cancellationToken);
            var lines = text.Split('\n');
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index].TrimEnd('\r');
                var hasTokenCount = line.Contains("\"token_count\"", StringComparison.Ordinal) ||
                                    line.Contains("\"tokenCount\"", StringComparison.Ordinal);
                var hasRateLimits = line.Contains("\"rate_limits\"", StringComparison.Ordinal) ||
                                    line.Contains("\"rateLimits\"", StringComparison.Ordinal);
                if (!hasTokenCount || !hasRateLimits)
                {
                    continue;
                }

                try
                {
                    return ParseSessionEvent(line);
                }
                catch (JsonException)
                {
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        throw new InvalidDataException("No Codex total rate-limit event was found in recent sessions.");
    }

    private static async Task<string> ReadTailAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        var offset = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (offset == 0)
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        return firstNewline >= 0 ? text[(firstNewline + 1)..] : "";
    }

    private static CodexQuotaSnapshot ParseRateLimits(JsonElement rateLimits, string source)
    {
        var limitId = ReadString(rateLimits, "limit_id") ?? ReadString(rateLimits, "limitId");
        if (!string.Equals(limitId, "codex", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Rate-limit payload is not the Codex total quota bucket.");
        }

        var primary = ParseWindow(rateLimits, "primary");
        var secondary = ParseWindow(rateLimits, "secondary");
        CodexQuotaWindow? shortWindow = null;
        CodexQuotaWindow? weeklyWindow = null;

        foreach (var window in new[] { primary, secondary })
        {
            if (window is null)
            {
                continue;
            }
            if (window.WindowMinutes >= 24 * 60)
            {
                weeklyWindow = window;
            }
            else if (window.WindowMinutes is not null)
            {
                shortWindow = window;
            }
        }

        if (primary is { WindowMinutes: null } && shortWindow is null)
        {
            shortWindow = primary;
        }
        if (secondary is { WindowMinutes: null } && weeklyWindow is null)
        {
            weeklyWindow = secondary;
        }
        if (shortWindow is null && weeklyWindow is null)
        {
            throw new InvalidDataException("Codex total quota has no usable windows.");
        }

        return new CodexQuotaSnapshot(shortWindow, weeklyWindow, source);
    }

    private static CodexQuotaWindow? ParseWindow(JsonElement parent, string name)
    {
        if (!TryGetObject(parent, name, out var window))
        {
            return null;
        }

        var used = ReadDouble(window, "used_percent") ?? ReadDouble(window, "usedPercent");
        if (used is null || !double.IsFinite(used.Value))
        {
            return null;
        }

        var minutes = ReadInt(window, "window_minutes") ??
                      ReadInt(window, "windowMinutes") ??
                      ReadInt(window, "windowDurationMins") ??
                      ReadInt(window, "window_duration_mins");
        var resetEpoch = ReadLong(window, "resets_at") ?? ReadLong(window, "resetsAt");
        DateTimeOffset? resetsAt = resetEpoch is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(resetEpoch.Value)
            : null;
        return new CodexQuotaWindow(used.Value, minutes, resetsAt);
    }

    private static string? FindCodexExecutable()
    {
        foreach (var variableName in new[]
        {
            "MPT_CODEX_APP_SERVER_EXE",
            "XBRD_CODEX_APP_SERVER_EXE"
        })
        {
            var configured = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }
        }

        var cached = Volatile.Read(ref _cachedCodexExecutable);
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
        {
            return cached;
        }

        var candidates = new List<FileInfo>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddCandidate(candidates, Path.Combine(localAppData, "OpenAI", "Codex", "codex.exe"));
            AddCandidates(candidates, Path.Combine(localAppData, "OpenAI", "Codex", "bin"), "codex.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddCandidate(candidates, "/opt/homebrew/bin/codex");
            AddCandidate(candidates, "/usr/local/bin/codex");
            AddCandidate(candidates, Path.Combine(userProfile, ".local", "bin", "codex"));
            AddCandidate(candidates, Path.Combine(userProfile, ".npm-global", "bin", "codex"));
            foreach (var applicationRoot in new[]
            {
                Path.Combine("/Applications", "Codex.app", "Contents"),
                Path.Combine(userProfile, "Applications", "Codex.app", "Contents")
            })
            {
                AddCandidate(candidates, Path.Combine(applicationRoot, "MacOS", "Codex"));
                AddCandidate(candidates, Path.Combine(applicationRoot, "MacOS", "codex"));
                AddCandidate(candidates, Path.Combine(applicationRoot, "Resources", "codex"));
                AddCandidates(candidates, Path.Combine(applicationRoot, "Resources"), "codex");
            }
        }

        foreach (var extensionsRoot in new[]
        {
            Path.Combine(userProfile, ".vscode", "extensions"),
            Path.Combine(userProfile, ".cursor", "extensions"),
            Path.Combine(userProfile, ".windsurf", "extensions")
        })
        {
            AddCandidates(candidates, extensionsRoot, executableName);
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            AddCandidate(candidates, Path.Combine(directory.Trim().Trim('"'), executableName));
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var resolved = candidates
            .Where(candidate => candidate.Exists)
            .GroupBy(candidate => candidate.FullName, pathComparer)
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .Select(candidate => candidate.FullName)
            .FirstOrDefault();
        if (resolved is not null)
        {
            Interlocked.Exchange(ref _cachedCodexExecutable, resolved);
        }
        return resolved;
    }

    private static void AddCandidate(List<FileInfo> candidates, string path)
    {
        try
        {
            candidates.Add(new FileInfo(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }
    }

    private static void AddCandidates(List<FileInfo> candidates, string root, string fileName)
    {
        try
        {
            if (Directory.Exists(root))
            {
                candidates.AddRange(
                    Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                        .Select(path => new FileInfo(path)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        if (TryGetValue(parent, name, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryGetValue(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryFindCodexLimit(JsonElement limits, out JsonElement codexLimit)
    {
        if (limits.ValueKind == JsonValueKind.Object)
        {
            if (TryGetObject(limits, "codex", out codexLimit))
            {
                return true;
            }

            foreach (var property in limits.EnumerateObject())
            {
                if (IsCodexLimit(property.Value))
                {
                    codexLimit = property.Value;
                    return true;
                }
            }
        }
        else if (limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (IsCodexLimit(item))
                {
                    codexLimit = item;
                    return true;
                }
            }
        }

        codexLimit = default;
        return false;
    }

    private static bool IsCodexLimit(JsonElement candidate) =>
        candidate.ValueKind == JsonValueKind.Object &&
        string.Equals(
            ReadString(candidate, "limit_id") ?? ReadString(candidate, "limitId"),
            "codex",
            StringComparison.Ordinal);

    private static string? ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static double? ReadDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }
        return null;
    }

    private static int? ReadInt(JsonElement parent, string name)
    {
        var value = ReadLong(parent, name);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static long? ReadLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }
        return null;
    }
}
