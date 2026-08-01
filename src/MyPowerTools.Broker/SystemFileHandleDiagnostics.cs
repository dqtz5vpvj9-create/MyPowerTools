using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace MyPowerTools.Broker;

public static class SystemFileHandleDiagnosticExecutor
{
    private const string ModuleId = "local-lag-cleaner";
    private const string ActionId = "system-file-handle-path-sample";
    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaximumBufferBytes = 512 * 1024 * 1024;
    private const uint ProcessDuplicateHandle = 0x00000040;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint FileNameOpened = 0x00000008;
    private const uint VolumeNameNt = 0x00000002;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    private const int MaximumPathChars = 32 * 1024;
    private const int MaximumRequestBytes = 64 * 1024;

    public static async Task<int> ExecuteAsync(
        string[] arguments,
        AuditLog audit,
        CancellationToken cancellationToken = default)
    {
        var token = GetOption(arguments, "--token") ?? "";
        var requestPath = GetOption(arguments, "--request-file") ?? "";
        var requestDigest = GetOption(arguments, "--digest") ?? "";
        var expectedBrokerHash = GetOption(arguments, "--broker-sha256") ?? "";
        var auditId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            var request = ReadAndValidateRequest(
                requestPath,
                token,
                requestDigest,
                expectedBrokerHash);
            using var operationGate = new Semaphore(
                initialCount: 1,
                maximumCount: 1,
                @"Global\MyPowerTools.SystemFileHandleDiagnostic.v1");
            if (!operationGate.WaitOne(TimeSpan.FromSeconds(10)))
            {
                AppendAudit(audit, auditId, token, "busy", "another diagnostic is active");
                return 4;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var debugPrivilege = DebugPrivilegeScope.Enable();
                var result = ReadPaths(
                    request.FileTypeIndex,
                    request.ExpectedFileHandles,
                    request.MaximumSamples,
                    debugPrivilege.Enabled,
                    cancellationToken);
                await WriteResultAsync(
                    request.ResultPath,
                    token,
                    requestDigest,
                    result,
                    cancellationToken).ConfigureAwait(false);
                AppendAudit(
                    audit,
                    auditId,
                    token,
                    "succeeded",
                    $"sampled={result.RequestedSamples}; resolved={result.ResolvedPathSamples}");
                return 0;
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            AppendAudit(audit, auditId, token, "cancelled", "diagnostic cancelled");
            return 5;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            Win32Exception or
            JsonException or
            OverflowException)
        {
            AppendAudit(
                audit,
                auditId,
                token,
                "rejected",
                exception.GetType().Name);
            return 3;
        }
    }

    private static DiagnosticRequest ReadAndValidateRequest(
        string requestPath,
        string token,
        string requestDigest,
        string expectedBrokerHash)
    {
        if (!OperatingSystem.IsWindows() ||
            !IsHex(token, 32) ||
            !IsHex(requestDigest, 64) ||
            !IsHex(expectedBrokerHash, 64) ||
            !Path.IsPathFullyQualified(requestPath))
        {
            throw new InvalidDataException("Invalid diagnostic request arguments.");
        }

        var expectedDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests",
            ModuleId));
        var fullRequestPath = Path.GetFullPath(requestPath);
        if (!string.Equals(
                Path.GetDirectoryName(fullRequestPath),
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(fullRequestPath),
                $"{token}.json",
                StringComparison.OrdinalIgnoreCase) ||
            WindowsProtectedExecutable.ContainsReparsePoint(fullRequestPath))
        {
            throw new InvalidDataException("Diagnostic request path is outside its fixed directory.");
        }

        var bytes = File.ReadAllBytes(fullRequestPath);
        if (bytes.Length is 0 or > MaximumRequestBytes ||
            !FixedHexEquals(Sha256(bytes), requestDigest))
        {
            throw new InvalidDataException("Diagnostic request digest is invalid.");
        }

        var root = JsonNode.Parse(
                       bytes,
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 8
                       })?.AsObject() ??
                   throw new InvalidDataException("Diagnostic request root must be an object.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "token",
            "moduleId",
            "action",
            "createdAt",
            "expiresAt",
            "fileTypeIndex",
            "expectedFileHandles",
            "maximumSamples",
            "broker"
        };
        if (root.Any(property => !allowed.Contains(property.Key)) ||
            root.Count != allowed.Count ||
            root["schemaVersion"]?.GetValue<int>() != 1 ||
            !string.Equals(root["token"]?.GetValue<string>(), token, StringComparison.Ordinal) ||
            !string.Equals(root["moduleId"]?.GetValue<string>(), ModuleId, StringComparison.Ordinal) ||
            !string.Equals(root["action"]?.GetValue<string>(), ActionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Diagnostic request fields are invalid.");
        }

        if (!DateTimeOffset.TryParse(
                root["createdAt"]?.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAt) ||
            !DateTimeOffset.TryParse(
                root["expiresAt"]?.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            throw new InvalidDataException("Diagnostic request lifetime is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        if (createdAt > now.AddSeconds(30) ||
            createdAt < now.AddMinutes(-6) ||
            expiresAt <= now ||
            expiresAt - createdAt > TimeSpan.FromMinutes(5))
        {
            throw new InvalidDataException("Diagnostic request has expired.");
        }

        var fileTypeIndex = root["fileTypeIndex"]?.GetValue<int>() ?? 0;
        var expectedFileHandles = root["expectedFileHandles"]?.GetValue<long>() ?? 0;
        var maximumSamples = root["maximumSamples"]?.GetValue<int>() ?? 0;
        if (fileTypeIndex is < 1 or > ushort.MaxValue ||
            expectedFileHandles is < 1 or > 50_000_000 ||
            maximumSamples is < 1 or > 512)
        {
            throw new InvalidDataException("Diagnostic sample bounds are invalid.");
        }

        var broker = root["broker"] as JsonObject ??
                     throw new InvalidDataException("Diagnostic broker identity is missing.");
        if (broker.Count != 2 ||
            broker.Any(property => property.Key is not ("path" or "sha256")))
        {
            throw new InvalidDataException("Diagnostic broker identity fields are invalid.");
        }

        var executablePath = Path.GetFullPath(Environment.ProcessPath ?? "");
        var approvedPath = broker["path"]?.GetValue<string>() ?? "";
        var approvedHash = broker["sha256"]?.GetValue<string>() ?? "";
        if (!Path.IsPathFullyQualified(approvedPath) ||
            !string.Equals(
                Path.GetFullPath(approvedPath),
                executablePath,
                StringComparison.OrdinalIgnoreCase) ||
            !FixedHexEquals(approvedHash, expectedBrokerHash) ||
            !FixedHexEquals(Sha256File(executablePath), expectedBrokerHash))
        {
            throw new InvalidDataException("Diagnostic broker identity changed after approval.");
        }

        return new DiagnosticRequest(
            checked((ushort)fileTypeIndex),
            checked((ulong)expectedFileHandles),
            maximumSamples,
            Path.Combine(expectedDirectory, $"{token}.result.json"));
    }

    private static unsafe DiagnosticResult ReadPaths(
        ushort fileTypeIndex,
        ulong expectedFileHandles,
        int maximumSamples,
        bool debugPrivilegeEnabled,
        CancellationToken cancellationToken)
    {
        using var buffer = QueryHandleTable();
        var headerBytes = checked(IntPtr.Size * 2);
        var entryBytes = Marshal.SizeOf<SystemHandleTableEntryInfoEx>();
        if (buffer.Length < headerBytes)
        {
            throw new InvalidDataException("System handle table header is incomplete.");
        }

        var reportedCount = IntPtr.Size == 8
            ? *(ulong*)buffer.Pointer
            : *(uint*)buffer.Pointer;
        var availableEntries =
            (ulong)(buffer.Length - headerBytes) / (ulong)entryBytes;
        if (reportedCount > availableEntries)
        {
            throw new InvalidDataException("System handle table count exceeds its buffer.");
        }

        var entries = (SystemHandleTableEntryInfoEx*)
            ((byte*)buffer.Pointer + headerBytes);
        ulong matchingCount = 0;
        for (ulong index = 0; index < reportedCount; index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var entry = entries[index];
            if (entry.UniqueProcessId.ToUInt64() == 4 &&
                entry.ObjectTypeIndex == fileTypeIndex)
            {
                matchingCount++;
            }
        }

        if (matchingCount == 0)
        {
            throw new InvalidDataException("The approved File object type has no PID 4 handles.");
        }

        var sampleCount = checked((int)Math.Min(
            matchingCount,
            (ulong)maximumSamples));
        var samples = new List<ulong>(sampleCount);
        ulong matchingIndex = 0;
        var nextTarget = 0UL;
        for (ulong index = 0;
             index < reportedCount && samples.Count < sampleCount;
             index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var entry = entries[index];
            if (entry.UniqueProcessId.ToUInt64() != 4 ||
                entry.ObjectTypeIndex != fileTypeIndex)
            {
                continue;
            }

            if (matchingIndex == nextTarget)
            {
                samples.Add(entry.HandleValue.ToUInt64());
                nextTarget = checked(
                    (ulong)samples.Count * matchingCount /
                    (ulong)sampleCount);
            }
            matchingIndex++;
        }

        var filterInstances = ReadFilterInstances();
        using var systemProcess = OpenProcess(
            ProcessDuplicateHandle,
            inheritHandle: false,
            processId: 4);
        if (systemProcess.IsInvalid)
        {
            var nativeError = Marshal.GetLastWin32Error();
            return new DiagnosticResult(
                matchingCount,
                samples.Count,
                0,
                0,
                0,
                debugPrivilegeEnabled,
                nativeError == 5,
                nativeError,
                nativeError == 5
                    ? $"Administrator Broker and SeDebugPrivilege were accepted, but Windows denied PROCESS_DUP_HANDLE for PID 4 with error 5. Existing System handle paths require a trusted kernel driver or kernel debugger. Enumerated {filterInstances.Count:n0} live minifilter-to-volume instances instead."
                    : $"Administrator Broker could not open PID 4 with PROCESS_DUP_HANDLE (Win32 {nativeError}: {new Win32Exception(nativeError).Message}). Enumerated {filterInstances.Count:n0} live minifilter-to-volume instances.",
                [],
                filterInstances);
        }

        var observations = new List<PathObservation>();
        var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var errorCounts = new Dictionary<int, int>();
        var duplicated = 0;
        foreach (var handleValue in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DuplicateHandle(
                    systemProcess,
                    new IntPtr(unchecked((long)handleValue)),
                    GetCurrentProcess(),
                    out var duplicatedHandle,
                    0,
                    inheritHandle: false,
                    DuplicateSameAccess))
            {
                var error = Marshal.GetLastWin32Error();
                errorCounts[error] = errorCounts.GetValueOrDefault(error) + 1;
                continue;
            }

            using (duplicatedHandle)
            {
                duplicated++;
                var kind = FileKind(
                    GetFileType(duplicatedHandle.DangerousGetHandle()));
                kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
                if (!string.Equals(kind, "Disk", StringComparison.Ordinal) &&
                    !string.Equals(kind, "Remote", StringComparison.Ordinal))
                {
                    continue;
                }

                var path = TryReadPath(duplicatedHandle);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    path = SanitizePath(path);
                    observations.Add(new PathObservation(
                        PathGroup(path),
                        kind,
                        path));
                }
            }
        }

        var groups = observations
            .GroupBy(
                item => (item.Group, item.Kind),
                new PathGroupKeyComparer())
            .Select(group => new PathGroupResult(
                group.Key.Group,
                group.Key.Kind,
                group.Count(),
                observations.Count == 0
                    ? 0
                    : Math.Round(group.Count() * 100d / observations.Count, 2),
                group.Select(item => item.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray()))
            .OrderByDescending(item => item.SampleCount)
            .ThenBy(item => item.PathGroup, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var errors = errorCounts.Count == 0
            ? ""
            : " Duplicate failures: " +
              string.Join(
                  ", ",
                  errorCounts
                      .OrderByDescending(item => item.Value)
                      .Select(item => $"{item.Key}×{item.Value}")) +
              ".";
        var kinds = kindCounts.Count == 0
            ? ""
            : " Types: " +
              string.Join(
                  ", ",
                  kindCounts
                      .OrderByDescending(item => item.Value)
                      .Select(item => $"{item.Key}={item.Value}")) +
              ".";
        var drift = matchingCount == expectedFileHandles
            ? ""
            : $" Handle count changed from {expectedFileHandles:n0} to {matchingCount:n0} before elevation.";
        return new DiagnosticResult(
            matchingCount,
            samples.Count,
            samples.Count,
            duplicated,
            observations.Count,
            debugPrivilegeEnabled,
            false,
            0,
            $"Elevated Broker uniformly sampled {samples.Count:n0} of {matchingCount:n0} PID 4 File handles; duplicated {duplicated:n0}, resolved {observations.Count:n0} opened paths into {groups.Length:n0} groups.{kinds}{errors}{drift}",
            groups,
            filterInstances);
    }

    private static IReadOnlyList<FilterInstanceResult> ReadFilterInstances()
    {
        var executable = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "fltmc.exe"));
        if (!File.Exists(executable))
        {
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("instances");
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000) || process.ExitCode != 0)
        {
            TryKill(process);
            return [];
        }
        if (output.Length > 1024 * 1024)
        {
            return [];
        }

        var lines = output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        var header = lines.FirstOrDefault(line =>
            line.TrimStart().StartsWith(
                "Filter",
                StringComparison.OrdinalIgnoreCase));
        var volumeColumn = header?.IndexOf(
            "Volume Name",
            StringComparison.OrdinalIgnoreCase) ?? -1;
        var altitudeColumn = header?.IndexOf(
            "Altitude",
            StringComparison.OrdinalIgnoreCase) ?? -1;
        var instanceColumn = header?.IndexOf(
            "Instance Name",
            StringComparison.OrdinalIgnoreCase) ?? -1;
        var frameColumn = header?.IndexOf(
            "Frame",
            StringComparison.OrdinalIgnoreCase) ?? -1;
        var statusColumn = header?.IndexOf(
            "VlStatus",
            StringComparison.OrdinalIgnoreCase) ?? -1;
        var fixedColumns = volumeColumn > 0 &&
                           altitudeColumn > volumeColumn &&
                           instanceColumn > altitudeColumn &&
                           frameColumn > instanceColumn &&
                           statusColumn > frameColumn;
        var rows = new List<FilterInstanceResult>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("-", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith(
                    "Filter",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string filterName;
            string volumeName;
            string altitude;
            string instanceName;
            string frame;
            string volumeStatus;
            if (fixedColumns)
            {
                filterName = ReadColumn(line, 0, volumeColumn);
                volumeName = ReadColumn(line, volumeColumn, altitudeColumn);
                altitude = ReadColumn(line, altitudeColumn, instanceColumn);
                instanceName = ReadColumn(line, instanceColumn, frameColumn);
                frame = ReadColumn(line, frameColumn, statusColumn);
                volumeStatus = ReadColumn(line, statusColumn, line.Length);
            }
            else
            {
                var values = line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);
                if (values.Length < 5)
                {
                    continue;
                }
                filterName = values[0];
                volumeName = values[1];
                altitude = values[2];
                instanceName = values[3];
                frame = values[4];
                volumeStatus = values.Length >= 6 ? values[5] : "";
            }

            if (string.IsNullOrWhiteSpace(filterName) ||
                string.IsNullOrWhiteSpace(volumeName) ||
                !double.TryParse(
                    altitude,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                continue;
            }
            rows.Add(new FilterInstanceResult(
                filterName,
                SanitizePath(volumeName),
                altitude,
                instanceName,
                frame,
                volumeStatus));
            if (rows.Count >= 4096)
            {
                break;
            }
        }
        return rows;
    }

    private static string ReadColumn(
        string line,
        int start,
        int end)
    {
        if (start < 0 || start >= line.Length || end <= start)
        {
            return "";
        }
        return line[start..Math.Min(end, line.Length)].Trim();
    }

    private static string SanitizePath(string value)
    {
        var userName = Environment.UserName;
        return string.IsNullOrWhiteSpace(userName)
            ? value
            : value.Replace(
                userName,
                "%USERNAME%",
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteResultAsync(
        string resultPath,
        string token,
        string requestDigest,
        DiagnosticResult result,
        CancellationToken cancellationToken)
    {
        if (File.Exists(resultPath))
        {
            throw new InvalidDataException("Diagnostic result already exists.");
        }

        byte[] bytes;
        using (var memory = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(
                       memory,
                       new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("token", token);
                writer.WriteString("requestDigest", requestDigest);
                writer.WriteString(
                    "capturedAtUtc",
                    DateTimeOffset.UtcNow.ToString(
                        "O",
                        CultureInfo.InvariantCulture));
                writer.WriteNumber("totalFileHandles", result.TotalFileHandles);
                writer.WriteNumber("requestedSamples", result.RequestedSamples);
                writer.WriteNumber("attemptedSamples", result.AttemptedSamples);
                writer.WriteNumber("duplicatedSamples", result.DuplicatedSamples);
                writer.WriteNumber(
                    "resolvedPathSamples",
                    result.ResolvedPathSamples);
                writer.WriteBoolean(
                    "debugPrivilegeEnabled",
                    result.DebugPrivilegeEnabled);
                writer.WriteBoolean(
                    "requiresKernelDriver",
                    result.RequiresKernelDriver);
                writer.WriteNumber("nativeErrorCode", result.NativeErrorCode);
                writer.WriteString("summary", result.Summary);
                writer.WriteStartArray("pathGroups");
                foreach (var group in result.Groups)
                {
                    writer.WriteStartObject();
                    writer.WriteString("pathGroup", group.PathGroup);
                    writer.WriteString("fileKind", group.FileKind);
                    writer.WriteNumber("sampleCount", group.SampleCount);
                    writer.WriteNumber(
                        "sampleSharePercent",
                        group.SampleSharePercent);
                    writer.WriteStartArray("examples");
                    foreach (var example in group.Examples)
                    {
                        writer.WriteStringValue(example);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("filterInstances");
                foreach (var instance in result.FilterInstances)
                {
                    writer.WriteStartObject();
                    writer.WriteString("filterName", instance.FilterName);
                    writer.WriteString("volumeName", instance.VolumeName);
                    writer.WriteString("altitude", instance.Altitude);
                    writer.WriteString("instanceName", instance.InstanceName);
                    writer.WriteString("frame", instance.Frame);
                    writer.WriteString("volumeStatus", instance.VolumeStatus);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }
            bytes = memory.ToArray();
        }
        var temporary = resultPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, resultPath, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static NativeBuffer QueryHandleTable()
    {
        var requestedBytes = 128 * 1024 * 1024;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new NativeBuffer(requestedBytes);
            var status = NtQuerySystemInformation(
                SystemExtendedHandleInformation,
                buffer.Pointer,
                checked((uint)buffer.Length),
                out var returnedLength);
            if (status == 0)
            {
                return buffer;
            }

            buffer.Dispose();
            if (status != StatusInfoLengthMismatch)
            {
                throw new InvalidOperationException(
                    $"NtQuerySystemInformation failed with NTSTATUS 0x{status:x8}.");
            }

            var grown = Math.Max(
                checked((long)returnedLength + 1024 * 1024),
                checked((long)requestedBytes * 2));
            if (grown > MaximumBufferBytes)
            {
                throw new InvalidDataException(
                    "System handle table exceeds the diagnostic safety limit.");
            }
            requestedBytes = checked((int)grown);
        }

        throw new InvalidOperationException(
            "System handle table did not stabilize after bounded retries.");
    }

    private static string TryReadPath(SafeFileHandle handle)
    {
        var capacity = 1024;
        while (capacity <= MaximumPathChars)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                builder,
                checked((uint)capacity),
                FileNameOpened | VolumeNameNt);
            if (length == 0)
            {
                return "";
            }
            if (length < capacity)
            {
                return builder.ToString();
            }
            capacity = checked((int)Math.Min(
                MaximumPathChars + 1L,
                (long)length + 1));
        }
        return "";
    }

    private static string PathGroup(string path)
    {
        var parts = path.Replace('/', '\\')
            .Trim()
            .Split(
                '\\',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "(empty)";
        }

        var keep = string.Equals(parts[0], "Device", StringComparison.OrdinalIgnoreCase)
            ? parts.Length >= 2 &&
              (string.Equals(parts[1], "NamedPipe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parts[1], "Mailslot", StringComparison.OrdinalIgnoreCase))
                ? Math.Min(2, parts.Length)
                : parts.Length >= 2 &&
                  string.Equals(parts[1], "Mup", StringComparison.OrdinalIgnoreCase)
                    ? Math.Min(4, parts.Length)
                    : Math.Min(3, parts.Length)
            : parts[0].EndsWith(":", StringComparison.OrdinalIgnoreCase)
                ? Math.Min(2, parts.Length)
                : string.Equals(parts[0], "?", StringComparison.Ordinal)
                    ? Math.Min(3, parts.Length)
                    : Math.Min(2, parts.Length);
        return "\\" + string.Join("\\", parts.Take(keep));
    }

    private static string FileKind(uint type) => type switch
    {
        0x0001 => "Disk",
        0x0002 => "Character",
        0x0003 => "Pipe",
        0x8000 => "Remote",
        _ => "Unknown"
    };

    private static void AppendAudit(
        AuditLog audit,
        string auditId,
        string token,
        string result,
        string detail)
    {
        audit.Append(new BrokerAuditEntry(
            auditId,
            DateTimeOffset.UtcNow,
            ModuleId,
            ActionId,
            "elevated",
            string.IsNullOrWhiteSpace(token) ? "invalid" : token[..Math.Min(8, token.Length)],
            "Bounded read-only PID 4 File path sampling",
            true,
            result,
            detail));
    }

    private static string? GetOption(string[] arguments, string name)
    {
        var index = Array.FindIndex(
            arguments,
            value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < arguments.Length
            ? arguments[index + 1]
            : null;
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool FixedHexEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        SafeProcessHandle sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathChars,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleTableEntryInfoEx
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(int length)
        {
            Length = length;
            Pointer = Marshal.AllocHGlobal(length);
        }

        public IntPtr Pointer { get; private set; }
        public int Length { get; }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
            {
                return;
            }
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private sealed class DebugPrivilegeScope : IDisposable
    {
        private readonly SafeFileHandle? _token;
        private readonly TokenPrivileges _previous;
        private readonly bool _restore;

        private DebugPrivilegeScope(
            SafeFileHandle? token,
            TokenPrivileges previous,
            bool enabled,
            bool restore)
        {
            _token = token;
            _previous = previous;
            Enabled = enabled;
            _restore = restore;
        }

        public bool Enabled { get; }

        public static DebugPrivilegeScope Enable()
        {
            if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TokenAdjustPrivileges | TokenQuery,
                    out var token))
            {
                return new DebugPrivilegeScope(null, default, false, false);
            }

            if (!LookupPrivilegeValueW(
                    null,
                    "SeDebugPrivilege",
                    out var luid))
            {
                token.Dispose();
                return new DebugPrivilegeScope(null, default, false, false);
            }

            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };
            if (!AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    ref requested,
                    Marshal.SizeOf<TokenPrivileges>(),
                    out var previous,
                    out _) ||
                Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                token.Dispose();
                return new DebugPrivilegeScope(null, default, false, false);
            }

            return new DebugPrivilegeScope(token, previous, true, true);
        }

        public void Dispose()
        {
            if (_token is null)
            {
                return;
            }
            if (_restore)
            {
                var previous = _previous;
                _ = AdjustTokenPrivileges(
                    _token,
                    disableAllPrivileges: false,
                    ref previous,
                    0,
                    out _,
                    out _);
            }
            _token.Dispose();
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out SafeFileHandle tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValueW(
            string? systemName,
            string name,
            out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            SafeFileHandle tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TokenPrivileges newState,
            int bufferLength,
            out TokenPrivileges previousState,
            out int returnLength);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    private sealed record DiagnosticRequest(
        ushort FileTypeIndex,
        ulong ExpectedFileHandles,
        int MaximumSamples,
        string ResultPath);

    private sealed record DiagnosticResult(
        ulong TotalFileHandles,
        int RequestedSamples,
        int AttemptedSamples,
        int DuplicatedSamples,
        int ResolvedPathSamples,
        bool DebugPrivilegeEnabled,
        bool RequiresKernelDriver,
        int NativeErrorCode,
        string Summary,
        IReadOnlyList<PathGroupResult> Groups,
        IReadOnlyList<FilterInstanceResult> FilterInstances);

    private sealed record PathGroupResult(
        string PathGroup,
        string FileKind,
        int SampleCount,
        double SampleSharePercent,
        IReadOnlyList<string> Examples);

    private sealed record PathObservation(
        string Group,
        string Kind,
        string Path);

    private sealed record FilterInstanceResult(
        string FilterName,
        string VolumeName,
        string Altitude,
        string InstanceName,
        string Frame,
        string VolumeStatus);

    private sealed class PathGroupKeyComparer :
        IEqualityComparer<(string Group, string Kind)>
    {
        public bool Equals(
            (string Group, string Kind) x,
            (string Group, string Kind) y) =>
            string.Equals(x.Group, y.Group, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Group, string Kind) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Group),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Kind));
    }
}
