using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MyPowerTools.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Platform.Abstractions;

namespace PasteImage.MyPowerTools;

public sealed partial class PasteImageModule : IMptModule
{
    private readonly Channel<MptModuleEvent> _events = Channel.CreateUnbounded<MptModuleEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private JsonObject _settings = DefaultSettings();
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private string _dataDirectory = "";
    private INotificationService? _notifications;
    private IClipboardImageService? _clipboard;
    private IKeyboardShortcutService? _keyboardShortcuts;
    private long _eventSequence;

    public string Id => "paste-image";
    public string PackageId => "paste-image";
    public Version Version => new(0, 1, 0);


    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        context.TryGetCapability<INotificationService>("notification.desktop", out _notifications);
        context.TryGetCapability<IClipboardImageService>("clipboard.image", out _clipboard);
        context.TryGetCapability<IKeyboardShortcutService>("keyboard.shortcut", out _keyboardShortcuts);
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        return ValueTask.FromResult(new InitializeResult(
            true,
            context.ProtocolVersion,
            ["status", "commands", "settings", "logs"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var sshPath = FindOpenSshExecutable();
        var clipboardAvailable = _clipboard is not null;
        var host = ReadSetting("remoteHost", "chris");
        var directory = ReadSetting("remoteDirectory", "/tmp");
        var checks = new[]
        {
            new HealthCheckSnapshot("openssh.ssh", "OpenSSH ssh", sshPath is not null, sshPath is null ? "The system OpenSSH client was not found." : "Available."),
            new HealthCheckSnapshot("clipboard.native", "Native clipboard", clipboardAvailable, clipboardAvailable ? "Available." : "The platform clipboard image provider is unavailable."),
            new HealthCheckSnapshot("destination.valid", "Remote destination", IsValidHost(host) && IsValidRemoteDirectory(directory), $"{host}:{directory}")
        };
        var ready = checks.All(check => check.Ok);
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            ready ? "running" : "degraded",
            ready ? $"Ready to upload clipboard images to {host}:{directory}." : string.Join("; ", checks.Where(check => !check.Ok).Select(check => check.Message)),
            DateTimeOffset.UtcNow,
            checks,
            (ulong)Math.Max(0, Interlocked.Read(ref _eventSequence))));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new MptCommandDescriptor(
                "paste-image.upload",
                Id,
                "Upload clipboard image",
                "Upload the current clipboard image over OpenSSH and copy its remote path",
                "action",
                Category: "Clipboard",
                TimeoutMs: 60000,
                Constraints: [MptOperationConstraints.RunsExternalProcesses],
                SupportsCancellation: true),
            new MptCommandDescriptor(
                "paste-image.inspect",
                Id,
                "Inspect Paste Image",
                "Read the configured destination and OpenSSH readiness",
                "action",
                Category: "Clipboard",
                TimeoutMs: 5000,
                SupportsCancellation: true),
            new MptCommandDescriptor(
                "paste-image.history",
                Id,
                "Recent Paste Image uploads",
                "Read recent upload records and local preview paths",
                "action",
                Category: "Clipboard",
                TimeoutMs: 5000,
                SupportsCancellation: true),
            new MptCommandDescriptor(
                "paste-image.clipboard.probe",
                Id,
                "Probe clipboard image",
                "Read and encode the current clipboard image without uploading it",
                "action",
                Category: "Clipboard",
                TimeoutMs: 10000,
                SupportsCancellation: true),
            new MptCommandDescriptor(
                "paste-image.notification.test",
                Id,
                "Test Paste Image notification",
                "Show a native desktop notification without uploading an image",
                "action",
                Category: "Clipboard",
                TimeoutMs: 5000,
                SupportsCancellation: true)
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (string.Equals(request.CommandId, "paste-image.inspect", StringComparison.OrdinalIgnoreCase))
        {
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var output = JsonSerializer.Serialize(new
            {
                status.State,
                status.Summary,
                remoteHost = ReadSetting("remoteHost", "chris"),
                remoteDirectory = ReadSetting("remoteDirectory", "/tmp"),
                uploadTimeoutSeconds = ReadTimeoutSeconds(),
                afterUploadShortcut = ReadAfterUploadShortcut(),
                keyboardShortcutAvailable = _keyboardShortcuts is not null,
                checks = status.Checks.Select(check => new { check.Id, check.Label, check.Ok, check.Message })
            });
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
        }

        if (string.Equals(request.CommandId, "paste-image.history", StringComparison.OrdinalIgnoreCase))
        {
            var history = await ReadHistoryAsync(cancellationToken).ConfigureAwait(false);
            var output = JsonSerializer.Serialize(new
            {
                items = history.Select(item => new
                {
                    item.RemotePath,
                    item.LocalPreviewPath,
                    item.UploadedAt,
                    item.Width,
                    item.Height,
                    item.SizeBytes
                })
            });
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
        }

        if (string.Equals(request.CommandId, "paste-image.clipboard.probe", StringComparison.OrdinalIgnoreCase))
        {
            var clipboard = _clipboard ??
                throw new InvalidOperationException("The platform clipboard image provider is unavailable.");
            var image = await clipboard.ReadPngAsync(cancellationToken).ConfigureAwait(false);
            var output = JsonSerializer.Serialize(new
            {
                image.Width,
                image.Height,
                SizeBytes = image.PngBytes.Length,
                image.UsedNativePng
            });
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
        }

        if (string.Equals(request.CommandId, "paste-image.notification.test", StringComparison.OrdinalIgnoreCase))
        {
            var published = await NotifyAsync(
                "Paste Image 通知测试",
                "系统通知已启用。",
                cancellationToken).ConfigureAwait(false);
            return published
                ? new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, "System notification published.")
                : Failed(request, MptErrorCodes.RuntimeUnavailable, "The desktop notification capability is unavailable.");
        }

        if (!string.Equals(request.CommandId, "paste-image.upload", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by Paste Image.");
        }

        var commandStartedUtc = DateTimeOffset.UtcNow;
        var profileWatch = Stopwatch.StartNew();
        try
        {
            var upload = await UploadClipboardImageAsync(profileWatch, cancellationToken).ConfigureAwait(false);
            var shortcut = await SendAfterUploadShortcutAsync(profileWatch).ConfigureAwait(false);
            PublishUploadEvent(upload, shortcut, request, commandStartedUtc, profileWatch.Elapsed.TotalMilliseconds);
            await NotifyAsync("Paste Image 上传成功", $"远端路径已复制：{upload.Item.RemotePath}", CancellationToken.None).ConfigureAwait(false);
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, upload.Item.RemotePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishEvent("upload.failed", "Image upload cancelled", "The clipboard image upload was cancelled.");
            await NotifyAsync("Paste Image 已取消", "剪贴板图片上传已取消。", CancellationToken.None).ConfigureAwait(false);
            return Failed(request, MptErrorCodes.CommandCancelled, "Clipboard image upload was cancelled.");
        }
        catch (Exception exception)
        {
            var message = MptLogRedactor.Redact(exception.Message);
            PublishEvent("upload.failed", "Image upload failed", message);
            await NotifyAsync("Paste Image 上传失败", FriendlyNotificationMessage(message), CancellationToken.None).ConfigureAwait(false);
            return Failed(request, MptErrorCodes.RuntimeUnavailable, message, retryable: true);
        }
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var moduleEvent in _events.Reader.ReadAllAsync(cancellationToken))
        {
            if (moduleEvent.Seq > cursor.LastEventSeq)
            {
                yield return moduleEvent;
            }
        }
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "required": ["remoteHost", "remoteDirectory", "uploadTimeoutSeconds"],
          "properties": {
            "remoteHost": {
              "type": "string",
              "title": "Remote SSH host",
              "description": "An OpenSSH config alias, host name, or user@host destination.",
              "default": "chris"
            },
            "remoteDirectory": {
              "type": "string",
              "title": "Remote image directory",
              "description": "An absolute POSIX path. The directory is created through SSH when needed.",
              "default": "/tmp"
            },
            "uploadTimeoutSeconds": {
              "type": "integer",
              "title": "Upload timeout",
              "minimum": 5,
              "maximum": 300,
              "default": 30
            },
            "afterUploadShortcut": {
              "type": "string",
              "title": "Upload-complete shortcut",
              "description": "A keyboard shortcut sent to the foreground app after the remote path is copied. Leave empty to disable.",
              "default": "Ctrl+Shift+V"
            }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, (JsonObject)_settings.DeepClone(), DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var values = SettingsJson.Merge(_settings, patch.Patch);
        var messages = ValidateSettings(values);
        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var merged = SettingsJson.Merge(DefaultSettings(), snapshot.Values);
        var messages = ValidateSettings(merged);
        if (messages.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", messages));
        }

        _settings = merged;
        return ValueTask.FromResult(snapshot with { Values = (JsonObject)_settings.DeepClone() });
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("paste-image.dashboard", "dashboard-card", "Paste Image", new JsonObject { ["state"] = "ready" }),
            new("paste-image.detail", "detail-page", "Paste Image", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private async Task<UploadResult> UploadClipboardImageAsync(Stopwatch profileWatch, CancellationToken cancellationToken)
    {
        var clipboard = _clipboard ??
            throw new InvalidOperationException("The platform clipboard image provider is unavailable.");
        var sshPath = FindOpenSshExecutable() ??
            throw new InvalidOperationException("The system OpenSSH client was not found.");
        var host = ReadSetting("remoteHost", "chris");
        var configuredDirectory = ReadSetting("remoteDirectory", "/tmp");
        var remoteDirectory = configuredDirectory == "/" ? "/" : configuredDirectory.TrimEnd('/');
        if (!IsValidHost(host) || !IsValidRemoteDirectory(remoteDirectory))
        {
            throw new InvalidOperationException("The configured SSH host or remote directory is invalid.");
        }

        var timeoutSeconds = ReadTimeoutSeconds();
        var fileName = $"paste-image-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
        var remotePath = remoteDirectory == "/" ? $"/{fileName}" : $"{remoteDirectory}/{fileName}";
        var uploadGateRequestedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
        if (!await _uploadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A clipboard image upload is already running.");
        }
        var uploadGateAcquiredMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
        try
        {
            var captureStartedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
            var image = await clipboard.ReadPngAsync(cancellationToken).ConfigureAwait(false);
            var captureCompletedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var ssh = await UploadPngViaSshAsync(
                sshPath,
                host,
                remoteDirectory,
                remotePath,
                image.PngBytes,
                timeoutSeconds,
                profileWatch,
                timeout.Token).ConfigureAwait(false);

            await clipboard.WriteTextAsync(remotePath, cancellationToken).ConfigureAwait(false);
            var clipboardWriteCompletedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
            var previewDirectory = Path.Combine(_dataDirectory, "history");
            Directory.CreateDirectory(previewDirectory);
            var previewPath = Path.Combine(previewDirectory, fileName);
            await File.WriteAllBytesAsync(previewPath, image.PngBytes, cancellationToken).ConfigureAwait(false);
            var previewWriteCompletedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
            var historyItem = new UploadHistoryItem(
                remotePath,
                previewPath,
                DateTimeOffset.UtcNow,
                image.Width,
                image.Height,
                image.PngBytes.Length);
            await AddHistoryAsync(historyItem, cancellationToken).ConfigureAwait(false);
            var historyWriteCompletedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
            return new UploadResult(
                historyItem,
                image.UsedNativePng,
                uploadGateRequestedMilliseconds,
                uploadGateAcquiredMilliseconds,
                captureStartedMilliseconds,
                captureCompletedMilliseconds,
                ssh.ProcessStartedMilliseconds,
                ssh.StdinWrittenMilliseconds,
                ssh.ProcessExitedMilliseconds,
                clipboardWriteCompletedMilliseconds,
                previewWriteCompletedMilliseconds,
                historyWriteCompletedMilliseconds);
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    private async Task<IReadOnlyList<UploadHistoryItem>> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadHistoryCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _historyGate.Release();
        }
    }

    private async Task AddHistoryAsync(UploadHistoryItem item, CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await ReadHistoryCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            items.Insert(0, item);
            var removed = items.Skip(20).ToArray();
            items = items.Take(20).ToList();
            foreach (var oldItem in removed)
            {
                TryDeletePreview(oldItem.LocalPreviewPath);
            }

            Directory.CreateDirectory(_dataDirectory);
            var historyPath = Path.Combine(_dataDirectory, "upload-history.json");
            var temporaryPath = historyPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, historyPath, overwrite: true);
        }
        finally
        {
            _historyGate.Release();
        }
    }

    private async Task<IReadOnlyList<UploadHistoryItem>> ReadHistoryCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return [];
        }

        var historyPath = Path.Combine(_dataDirectory, "upload-history.json");
        if (!File.Exists(historyPath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(historyPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<UploadHistoryItem>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void TryDeletePreview(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<SshTransferProfile> UploadPngViaSshAsync(
        string sshPath,
        string host,
        string remoteDirectory,
        string remotePath,
        byte[] pngBytes,
        int timeoutSeconds,
        Stopwatch profileWatch,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sshPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-T",
            "-o", "BatchMode=yes",
            "-o", $"ConnectTimeout={Math.Min(timeoutSeconds, 30)}",
            "-o", "ClearAllForwardings=yes",
            "-o", "LogLevel=ERROR",
            host,
            $"mkdir -p -- {remoteDirectory} && cat > {remotePath}"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ssh.");
        var processStartedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        double stdinWrittenMilliseconds;
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(pngBytes, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            stdinWrittenMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        var stderr = MptLogRedactor.Redact((await stderrTask.ConfigureAwait(false)).Trim());
        if (process.ExitCode != 0)
        {
            throw new IOException($"ssh exited with code {process.ExitCode}: {stderr}");
        }
        return new SshTransferProfile(
            processStartedMilliseconds,
            stdinWrittenMilliseconds,
            profileWatch.Elapsed.TotalMilliseconds);
    }

    private static string? FindOpenSshExecutable()
    {
        var path = OperatingSystem.IsWindows()
            ? Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"))
            : "/usr/bin/ssh";
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        return path;
    }

    private static IReadOnlyList<string> ValidateSettings(JsonObject values)
    {
        var messages = new List<string>();
        var host = SettingsJson.ReadString(values, "remoteHost") ?? "";
        var directory = SettingsJson.ReadString(values, "remoteDirectory") ?? "";
        if (!IsValidHost(host))
        {
            messages.Add("remoteHost must be an SSH alias, host name, or user@host value containing letters, digits, dots, underscores, and hyphens.");
        }
        if (!IsValidRemoteDirectory(directory))
        {
            messages.Add("remoteDirectory must be an absolute POSIX path without parent traversal, spaces, or shell metacharacters.");
        }
        if (!TryReadInteger(values, "uploadTimeoutSeconds", out var timeout) || timeout is < 5 or > 300)
        {
            messages.Add("uploadTimeoutSeconds must be between 5 and 300.");
        }
        var afterUploadShortcut = SettingsJson.ReadString(values, "afterUploadShortcut")?.Trim() ?? "";
        if (afterUploadShortcut.Length > 0 &&
            !KeyboardShortcutGesture.TryParse(afterUploadShortcut, requireModifier: false, out _, out var shortcutError))
        {
            messages.Add($"afterUploadShortcut is invalid: {shortcutError}");
        }
        return messages;
    }

    private string ReadSetting(string name, string fallback)
    {
        return SettingsJson.ReadString(_settings, name) is { Length: > 0 } value ? value.Trim() : fallback;
    }

    private int ReadTimeoutSeconds()
    {
        return TryReadInteger(_settings, "uploadTimeoutSeconds", out var timeout) && timeout is >= 5 and <= 300 ? timeout : 30;
    }

    private string ReadAfterUploadShortcut()
    {
        var value = SettingsJson.ReadString(_settings, "afterUploadShortcut");
        value = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        if (value.Length == 0)
        {
            return "";
        }

        return KeyboardShortcutGesture.TryParse(value, requireModifier: false, out var parsed, out _)
            ? parsed!.NormalizedGesture
            : value;
    }

    private static bool TryReadInteger(JsonObject values, string name, out int value)
    {
        value = 0;
        try
        {
            return values[name] is not null && (value = values[name]!.GetValue<int>()) > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsValidHost(string value) => HostPattern().IsMatch(value.Trim());

    private static bool IsValidRemoteDirectory(string value)
    {
        value = value.Trim();
        return RemoteDirectoryPattern().IsMatch(value) &&
               !value.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal);
    }

    private static JsonObject DefaultSettings() => new()
    {
        ["remoteHost"] = "chris",
        ["remoteDirectory"] = "/tmp",
        ["uploadTimeoutSeconds"] = 30,
        ["afterUploadShortcut"] = "Ctrl+Shift+V"
    };

    private void PublishEvent(string type, string title, string message, string? remotePath = null)
    {
        var payload = new JsonObject
        {
            ["title"] = title,
            ["message"] = message
        };
        if (!string.IsNullOrWhiteSpace(remotePath))
        {
            payload["remotePath"] = remotePath;
        }

        var sequence = (ulong)Interlocked.Increment(ref _eventSequence);
        _events.Writer.TryWrite(new MptModuleEvent(Id, sequence, type, DateTimeOffset.UtcNow, payload));
    }

    private void PublishUploadEvent(
        UploadResult upload,
        AfterUploadShortcutProfile shortcut,
        CommandRequest request,
        DateTimeOffset commandStartedUtc,
        double moduleEventPublishedMilliseconds)
    {
        var item = upload.Item;
        var payload = new JsonObject
        {
            ["title"] = "Image uploaded",
            ["message"] = $"Path copied: {item.RemotePath}",
            ["remotePath"] = item.RemotePath,
            ["localPreviewPath"] = item.LocalPreviewPath,
            ["uploadedAt"] = item.UploadedAt.ToString("O"),
            ["width"] = item.Width,
            ["height"] = item.Height,
            ["sizeBytes"] = item.SizeBytes,
            ["profileId"] = request.InvocationId,
            ["hotkeyReceivedUtc"] = ReadProfileValue(request.Args, "__mptHotkeyReceivedUtc"),
            ["commandDispatchUtc"] = ReadProfileValue(request.Args, "__mptCommandDispatchUtc"),
            ["commandStartedUtc"] = commandStartedUtc.ToString("O"),
            ["moduleEventPublishedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["afterUploadShortcut"] = shortcut.Gesture,
            ["afterUploadShortcutAttempted"] = shortcut.Attempted,
            ["afterUploadShortcutSent"] = shortcut.Sent,
            ["afterUploadShortcutState"] = shortcut.State,
            ["afterUploadShortcutMessage"] = shortcut.Message,
            ["afterUploadShortcutMilliseconds"] = RoundProfile(shortcut.Milliseconds),
            ["uploadGateRequestedMilliseconds"] = RoundProfile(upload.UploadGateRequestedMilliseconds),
            ["uploadGateAcquiredMilliseconds"] = RoundProfile(upload.UploadGateAcquiredMilliseconds),
            ["captureStartedMilliseconds"] = RoundProfile(upload.CaptureStartedMilliseconds),
            ["captureCompletedMilliseconds"] = RoundProfile(upload.CaptureCompletedMilliseconds),
            ["sshProcessStartedMilliseconds"] = RoundProfile(upload.SshProcessStartedMilliseconds),
            ["sshStdinWrittenMilliseconds"] = RoundProfile(upload.SshStdinWrittenMilliseconds),
            ["sshProcessExitedMilliseconds"] = RoundProfile(upload.SshProcessExitedMilliseconds),
            ["clipboardWriteCompletedMilliseconds"] = RoundProfile(upload.ClipboardWriteCompletedMilliseconds),
            ["previewWriteCompletedMilliseconds"] = RoundProfile(upload.PreviewWriteCompletedMilliseconds),
            ["historyWriteCompletedMilliseconds"] = RoundProfile(upload.HistoryWriteCompletedMilliseconds),
            ["moduleEventPublishedMilliseconds"] = RoundProfile(moduleEventPublishedMilliseconds),
            ["captureMilliseconds"] = RoundProfile(upload.CaptureCompletedMilliseconds - upload.CaptureStartedMilliseconds),
            ["transferMilliseconds"] = RoundProfile(upload.SshProcessExitedMilliseconds - upload.SshProcessStartedMilliseconds),
            ["totalMilliseconds"] = RoundProfile(moduleEventPublishedMilliseconds),
            ["usedNativePng"] = upload.UsedNativePng
        };
        var sequence = (ulong)Interlocked.Increment(ref _eventSequence);
        _events.Writer.TryWrite(new MptModuleEvent(Id, sequence, "upload.alert", DateTimeOffset.UtcNow, payload));
    }

    private static string ReadProfileValue(JsonObject args, string name) =>
        args[name]?.GetValue<string>() ?? "";

    private static double RoundProfile(double value) => Math.Round(value, 2);

    private async Task<AfterUploadShortcutProfile> SendAfterUploadShortcutAsync(Stopwatch profileWatch)
    {
        var gesture = ReadAfterUploadShortcut();
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return new AfterUploadShortcutProfile("", false, false, "disabled", "After-upload shortcut is disabled.", 0);
        }

        if (_keyboardShortcuts is null)
        {
            return new AfterUploadShortcutProfile(
                gesture,
                false,
                false,
                "unavailable",
                "The keyboard shortcut capability is unavailable on this platform.",
                0);
        }

        var startedMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
        try
        {
            var result = await _keyboardShortcuts.SendAsync(gesture, CancellationToken.None).ConfigureAwait(false);
            return new AfterUploadShortcutProfile(
                gesture,
                true,
                result.Success,
                result.State,
                result.Message,
                profileWatch.Elapsed.TotalMilliseconds - startedMilliseconds);
        }
        catch (Exception exception)
        {
            return new AfterUploadShortcutProfile(
                gesture,
                true,
                false,
                "failed",
                MptLogRedactor.Redact(exception.Message),
                profileWatch.Elapsed.TotalMilliseconds - startedMilliseconds);
        }
    }

    private async Task<bool> NotifyAsync(string title, string body, CancellationToken cancellationToken)
    {
        if (_notifications is null) return false;
        try
        {
            await _notifications.PublishAsync(title, body, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            PublishEvent("notification.failed", "System notification failed", MptLogRedactor.Redact(exception.Message));
            return false;
        }
    }

    private static string FriendlyNotificationMessage(string message) =>
        message.Contains("No image is available in the clipboard", StringComparison.OrdinalIgnoreCase)
            ? "剪贴板中没有图片，请先复制一张图片。"
            : message;

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message, bool retryable = false)
    {
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            "",
            new MptRuntimeError(code, message, retryable));
    }

    [GeneratedRegex("^(?:[A-Za-z0-9._-]+@)?[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex("^/[A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoteDirectoryPattern();

    private sealed record UploadResult(
        UploadHistoryItem Item,
        bool UsedNativePng,
        double UploadGateRequestedMilliseconds,
        double UploadGateAcquiredMilliseconds,
        double CaptureStartedMilliseconds,
        double CaptureCompletedMilliseconds,
        double SshProcessStartedMilliseconds,
        double SshStdinWrittenMilliseconds,
        double SshProcessExitedMilliseconds,
        double ClipboardWriteCompletedMilliseconds,
        double PreviewWriteCompletedMilliseconds,
        double HistoryWriteCompletedMilliseconds);

    private sealed record SshTransferProfile(
        double ProcessStartedMilliseconds,
        double StdinWrittenMilliseconds,
        double ProcessExitedMilliseconds);

    private sealed record AfterUploadShortcutProfile(
        string Gesture,
        bool Attempted,
        bool Sent,
        string State,
        string Message,
        double Milliseconds);

    private sealed record UploadHistoryItem(
        string RemotePath,
        string LocalPreviewPath,
        DateTimeOffset UploadedAt,
        int Width,
        int Height,
        long SizeBytes);

}
