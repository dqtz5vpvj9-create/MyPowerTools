using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MyPowerTools.Packaging.Ota;

/// <summary>Validated full-package entry of a macOS channel feed.</summary>
public sealed record MacFeedPackage(
    string Version,
    string Channel,
    string Asset,
    string Sha256,
    long Size,
    string ManifestAsset,
    string ManifestSha256,
    bool Signed);

/// <summary>A process whose executable lives inside an app bundle.</summary>
public sealed record MacBundleProcess(int ProcessId, string ExecutablePath);

/// <summary>
/// Platform-independent rules of the macOS installer: feed and manifest validation, version
/// ordering, signature verification, architecture detection from tool output, process-list
/// parsing and LaunchAgent plist generation. Everything here is a pure function so the rules
/// that <c>ota-update-macos.ps1</c> / <c>install-macos-base.ps1</c> encoded stay unit tested.
/// </summary>
public static class MacInstallLogic
{
    public const string Repository = "dqtz5vpvj9-create/MyPowerTools";
    public const string EmbeddedPublicKeyHex = "0288efe271c9788b64eca7788fb074da696a08c890fb534ddef549a8648a1b4a";
    public const string BundleIdentifier = "com.mypowertools.desktop";
    public const string FeedKind = "mypowertools-ota-channel-feed";
    public const string ManifestKind = "mypowertools-ota-file-manifest";
    public const string ProductName = "MyPowerTools";
    public const string ServiceManagerLabel = "com.mypowertools.servicemanager";
    public const string RunnerLabel = "com.mypowertools.runner";
    public const string RunnerSocketFileName = "mypowertools.runner.hostcontrol.sock";
    public const string LsRegisterPath =
        "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    /// <summary>LaunchAgent labels in the order install-macos-base.ps1 writes them.</summary>
    public static IReadOnlyList<string> AgentLabels { get; } = [ServiceManagerLabel, RunnerLabel];

    /// <summary>Executables a valid bundle must ship, relative to the bundle root.</summary>
    public static IReadOnlyList<string> RequiredExecutables { get; } =
    [
        "Contents/MacOS/MyPowerTools",
        "Contents/MacOS/Helpers/MyPowerTools Shell.app/Contents/MacOS/MyPowerTools.Shell.Avalonia",
        "Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner",
        "Contents/MacOS/Helpers/MyPowerTools ServiceManager.app/Contents/MacOS/MyPowerTools.ServiceManager",
        "Contents/MacOS/Helpers/MyPowerTools Remote Notifications.app/Contents/MacOS/RemoteNotifications.Service"
    ];

    /// <summary>Payload directories a valid bundle must ship, relative to the bundle root.</summary>
    public static IReadOnlyList<string> RequiredDirectories { get; } =
        ["Contents/MacOS/modules", "Contents/MacOS/ServiceUnits"];

    private static readonly Regex VersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ProcessLinePattern = new("^\\s*(\\d+)\\s+(.+)$", RegexOptions.CultureInvariant);

    public static bool IsValidVersion(string? value) => value is not null && VersionPattern.IsMatch(value);

    public static bool IsSha256(string? value) => value is not null && Sha256Pattern.IsMatch(value);

    public static bool IsPublicKeyHex(string? value) => IsSha256(value);

    /// <summary>Compare-OtaVersion: strict three-part numeric comparison.</summary>
    public static int CompareVersion(string left, string right)
    {
        if (!IsValidVersion(left) || !IsValidVersion(right))
        {
            throw new FormatException($"版本号格式无效，无法比较：'{left}' 与 '{right}'。");
        }

        var l = left.Split('.');
        var r = right.Split('.');
        for (var index = 0; index < 3; index++)
        {
            var a = long.Parse(l[index], CultureInfo.InvariantCulture);
            var b = long.Parse(r[index], CultureInfo.InvariantCulture);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Decides whether the feed version should be installed. A missing installation always
    /// installs (<c>not-installed</c>); otherwise the ota-update-macos.ps1 rules apply.
    /// </summary>
    public static (bool Available, string Reason) DecideUpdate(
        bool installed,
        string currentVersion,
        string latestVersion,
        bool force)
    {
        if (!installed)
        {
            return (true, "not-installed");
        }

        var compare = CompareVersion(latestVersion, currentVersion);
        if (compare > 0)
        {
            return (true, "update-available");
        }

        if (force)
        {
            return (true, "forced");
        }

        return (false, compare == 0 ? "up-to-date" : "downgrade-blocked");
    }

    /// <summary>Assert-MacFeed: schema, kind, channel, version, platform assets, no deltas.</summary>
    public static MacFeedPackage ValidateFeed(JsonNode? feed, string channel, string runtimeIdentifier)
    {
        if (feed is not JsonObject root)
        {
            throw new InvalidDataException("更新源内容不是有效的 JSON 对象。");
        }

        if (ReadInt(root["schemaVersion"]) != 1 || ReadString(root["kind"]) != FeedKind)
        {
            throw new InvalidDataException("更新源格式不受支持（schemaVersion/kind 不匹配）。");
        }

        var feedChannel = ReadString(root["channel"]);
        if (!string.Equals(feedChannel, channel, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"更新源渠道 '{feedChannel}' 与请求的渠道 '{channel}' 不一致。");
        }

        var version = ReadString(root["version"]);
        if (!IsValidVersion(version))
        {
            throw new InvalidDataException($"更新源包含无效的版本号 '{version}'。");
        }

        var full = root["full"] as JsonObject
            ?? throw new InvalidDataException("更新源缺少完整安装包信息（full）。");
        var expectedArchive = OtaFeedLayout.FullPackageAsset(runtimeIdentifier);
        var expectedManifest = OtaFeedLayout.FileManifestAsset(runtimeIdentifier);
        var asset = ReadString(full["asset"]);
        var manifestAsset = ReadString(full["manifestAsset"]);
        if (asset != expectedArchive)
        {
            throw new InvalidDataException($"更新源中的安装包与本机架构不符：应为 {expectedArchive}，实际为 {asset}。");
        }

        if (manifestAsset != expectedManifest)
        {
            throw new InvalidDataException($"更新源中的文件清单与本机架构不符：应为 {expectedManifest}，实际为 {manifestAsset}。");
        }

        var sha256 = ReadString(full["sha256"]);
        var manifestSha256 = ReadString(full["manifestSha256"]);
        var size = ReadLong(full["size"]);
        if (!IsSha256(sha256) || !IsSha256(manifestSha256) || size is null or <= 0)
        {
            throw new InvalidDataException("更新源中的安装包元数据无效（sha256/size）。");
        }

        if (root["deltas"] is JsonArray { Count: > 0 } || root["deltas"] is JsonObject)
        {
            throw new InvalidDataException("macOS 更新源不得包含增量（delta）安装包。");
        }

        var signed = root["signing"] is JsonObject signing && ReadBool(signing["signed"]);
        return new MacFeedPackage(
            version,
            channel,
            asset,
            sha256.ToLowerInvariant(),
            size.Value,
            manifestAsset,
            manifestSha256.ToLowerInvariant(),
            signed);
    }

    /// <summary>Assert-MacManifest: the downloaded file manifest belongs to the selected release.</summary>
    public static void ValidateManifest(JsonNode? manifest, string expectedVersion)
    {
        if (manifest is not JsonObject root ||
            ReadInt(root["schemaVersion"]) != 1 ||
            ReadString(root["kind"]) != ManifestKind ||
            ReadString(root["product"]) != ProductName ||
            ReadString(root["version"]) != expectedVersion)
        {
            throw new InvalidDataException("下载的文件清单与所选版本不一致（kind/product/version 校验失败）。");
        }
    }

    /// <summary>
    /// Assert-FeedSignature. The signature covers the UTF-8 bytes of the feed text exactly as the
    /// pwsh updater read it (<c>ReadAllText</c> drops a BOM), so a byte-order mark is ignored.
    /// </summary>
    public static void VerifyFeedSignature(
        byte[] feedBytes,
        string? signatureBase64,
        string? publicKeyHex,
        bool feedSigned,
        bool allowUnsigned)
    {
        if (!feedSigned)
        {
            if (allowUnsigned)
            {
                return;
            }

            throw new InvalidDataException("更新源未签名，已拒绝。仅在本地或 nightly 验证时才可允许未签名更新源。");
        }

        if (!IsPublicKeyHex(publicKeyHex))
        {
            throw new InvalidDataException("已签名的更新源需要一个受信任的 64 位十六进制 Ed25519 公钥。");
        }

        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            throw new InvalidDataException("已签名的更新源缺少分离签名（.sig）。");
        }

        bool valid;
        try
        {
            var text = new UTF8Encoding(false).GetString(StripUtf8Bom(feedBytes));
            valid = Mpt.Ed25519.Verify(
                Encoding.UTF8.GetBytes(text),
                Convert.FromBase64String(signatureBase64.Trim()),
                Convert.FromHexString(publicKeyHex!));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new InvalidDataException($"更新源签名校验失败：{exception.Message}");
        }

        if (!valid)
        {
            throw new InvalidDataException("更新源签名校验失败：签名与更新源内容不匹配。");
        }
    }

    public static byte[] StripUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
    }

    /// <summary>
    /// Maps <c>lipo -archs</c> output to a runtime identifier. Only a single-architecture
    /// launcher decides; a universal or unreadable binary returns <see langword="null"/>.
    /// </summary>
    public static string? RuntimeIdentifierFromLipo(string? lipoOutput)
    {
        if (string.IsNullOrWhiteSpace(lipoOutput))
        {
            return null;
        }

        var tokens = lipoOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var arm = tokens.Contains("arm64", StringComparer.Ordinal);
        var intel = tokens.Contains("x86_64", StringComparer.Ordinal);
        if (arm && !intel)
        {
            return OtaFeedLayout.OsxArm64;
        }

        if (intel && !arm)
        {
            return OtaFeedLayout.OsxX64;
        }

        return null;
    }

    /// <summary>
    /// Maps <c>sysctl -n hw.optional.arm64</c> to a runtime identifier. The sysctl reports the
    /// hardware even under Rosetta; Intel Macs do not know the key and fail.
    /// </summary>
    public static string RuntimeIdentifierFromSysctl(int exitCode, string? output)
    {
        return exitCode == 0 && output?.Trim() == "1" ? OtaFeedLayout.OsxArm64 : OtaFeedLayout.OsxX64;
    }

    /// <summary>
    /// Parses <c>ps -o pid=,comm=</c> output (macOS prints the executable path as comm) and
    /// returns the processes running from inside <paramref name="bundlePath"/>, excluding
    /// launchd, the given process ids, and anything whose path merely resembles the bundle.
    /// </summary>
    public static IReadOnlyList<MacBundleProcess> SelectBundleProcesses(
        string? psOutput,
        string bundlePath,
        IReadOnlyCollection<int> excludedProcessIds)
    {
        var prefix = bundlePath.TrimEnd('/') + "/";
        var seen = new SortedDictionary<int, MacBundleProcess>();
        foreach (var rawLine in (psOutput ?? string.Empty).Split('\n'))
        {
            var match = ProcessLinePattern.Match(rawLine.TrimEnd('\r'));
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var executable = match.Groups[2].Value.TrimEnd();
            if (pid <= 1 ||
                excludedProcessIds.Contains(pid) ||
                !executable.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            seen[pid] = new MacBundleProcess(pid, executable);
        }

        return [.. seen.Values];
    }

    /// <summary>ProgramArguments of the two LaunchAgents, exactly as install-macos-base.ps1 writes them.</summary>
    public static IReadOnlyList<string> LaunchAgentProgramArguments(string label, string targetApp, string dataRoot)
    {
        var macRoot = MacRoot(targetApp);
        var helpersRoot = Path.Combine(macRoot, "Helpers");
        return label switch
        {
            ServiceManagerLabel =>
            [
                Path.Combine(helpersRoot, "MyPowerTools ServiceManager.app/Contents/MacOS/MyPowerTools.ServiceManager"),
                "--data-root",
                dataRoot,
                "--deploy-root",
                Path.Combine(macRoot, "ServiceUnits")
            ],
            RunnerLabel =>
            [
                Path.Combine(helpersRoot, "MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner"),
                "--modules",
                Path.Combine(macRoot, "modules"),
                "--data-root",
                dataRoot
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown MyPowerTools LaunchAgent label.")
        };
    }

    public static string MacRoot(string targetApp) => Path.Combine(targetApp, "Contents", "MacOS");

    /// <summary>The LaunchAgent plist text, byte for byte what Write-LaunchAgent produces.</summary>
    public static string BuildLaunchAgentPlist(
        string label,
        IReadOnlyList<string> programArguments,
        string workingDirectory,
        string logsRoot)
    {
        var arguments = string.Concat(programArguments.Select(argument => $"<string>{Escape(argument)}</string>"));
        var lines = new[]
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            "<plist version=\"1.0\">",
            "<dict>",
            $"  <key>Label</key><string>{label}</string>",
            $"  <key>ProgramArguments</key><array>{arguments}</array>",
            $"  <key>WorkingDirectory</key><string>{Escape(workingDirectory)}</string>",
            "  <key>RunAtLoad</key><true/>",
            "  <key>KeepAlive</key><true/>",
            "  <key>ProcessType</key><string>Background</string>",
            $"  <key>StandardOutPath</key><string>{Escape(Path.Combine(logsRoot, label + ".log"))}</string>",
            $"  <key>StandardErrorPath</key><string>{Escape(Path.Combine(logsRoot, label + ".error.log"))}</string>",
            "</dict>",
            "</plist>"
        };
        return string.Join('\n', lines);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    /// <summary>
    /// Extracts the program path from <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c>: the
    /// <c>program = …</c> line, or the first entry of the <c>arguments = { … }</c> block.
    /// </summary>
    public static string? ParseLaunchctlProgram(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output.Split('\n').Select(line => line.Trim()).ToArray();
        foreach (var line in lines)
        {
            if (line.StartsWith("program = ", StringComparison.Ordinal))
            {
                var value = line["program = ".Length..].Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("arguments = {", StringComparison.Ordinal) &&
                index + 1 < lines.Length &&
                lines[index + 1].Length > 0 &&
                lines[index + 1] != "}")
            {
                return lines[index + 1];
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> is strictly inside <paramref name="root"/> (ordinal).</summary>
    public static bool IsInside(string? path, string root)
    {
        return path is not null && path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    /// <summary>Reads a string value from an XML property list (fallback when plutil is unavailable).</summary>
    public static string? ReadPlistString(string plistXml, string key)
    {
        try
        {
            var document = XDocument.Parse(plistXml, LoadOptions.None);
            var dict = document.Root?.Element("dict");
            if (dict is null)
            {
                return null;
            }

            var elements = dict.Elements().ToList();
            for (var index = 0; index + 1 < elements.Count; index++)
            {
                if (elements[index].Name == "key" && elements[index].Value == key &&
                    elements[index + 1].Name == "string")
                {
                    return elements[index + 1].Value.Trim();
                }
            }
        }
        catch (System.Xml.XmlException)
        {
        }

        return null;
    }

    /// <summary>The installed-release.json document, same schema as ota-update-macos.ps1.</summary>
    public static JsonObject BuildInstalledRelease(
        string version,
        string channel,
        string installDir,
        string dataRoot,
        string manifestSha256,
        string runtimeIdentifier,
        DateTimeOffset installedAt)
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["product"] = ProductName,
            ["version"] = version,
            ["channel"] = channel,
            ["installedAt"] = installedAt.ToString("O", CultureInfo.InvariantCulture),
            ["installDir"] = installDir,
            ["dataRoot"] = dataRoot,
            ["repository"] = "https://github.com/" + Repository,
            ["manifestPath"] = "installed-files.manifest.json",
            ["manifestSha256"] = manifestSha256,
            ["packageKind"] = "full",
            ["distributionMode"] = "full",
            ["runtimeIdentifier"] = runtimeIdentifier
        };
    }

    /// <summary>The stable feed URL, or null for channels resolved through the GitHub API.</summary>
    public static string StableFeedUrl(string runtimeIdentifier)
    {
        return $"https://github.com/{Repository}/releases/latest/download/" +
               OtaFeedLayout.ChannelFeedAsset("stable", runtimeIdentifier);
    }

    /// <summary>Finds <paramref name="assetName"/> in a GitHub <c>releases</c> API response.</summary>
    public static string? FindReleaseAssetUrl(JsonNode? releases, string assetName)
    {
        if (releases is not JsonArray array)
        {
            return null;
        }

        foreach (var release in array)
        {
            if (release?["assets"] is not JsonArray assets)
            {
                continue;
            }

            foreach (var asset in assets)
            {
                if (ReadString(asset?["name"]) == assetName)
                {
                    var url = ReadString(asset?["browser_download_url"]);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The directory part of a feed URL, where release assets are downloaded from.</summary>
    public static string FeedBaseUrl(string feedUrl)
    {
        var index = feedUrl.LastIndexOf('/');
        return index <= 0 ? feedUrl : feedUrl[..index];
    }

    public static int? Percent(long received, long? total)
    {
        if (total is null or <= 0)
        {
            return null;
        }

        return (int)Math.Clamp(received * 100 / total.Value, 0, 100);
    }

    public static string Sha256Hex(Stream stream)
    {
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256Hex(stream);
    }

    internal static string ReadString(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            return value.ToJsonString();
        }

        return string.Empty;
    }

    private static int? ReadInt(JsonNode? node)
    {
        var number = ReadLong(node);
        return number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<double>(out var real) && real == Math.Floor(real))
        {
            return (long)real;
        }

        return value.TryGetValue<string>(out var text) &&
               long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text) &&
               bool.TryParse(text, out var parsed) && parsed;
    }
}
