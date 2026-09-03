using System.Diagnostics;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacUserNotificationService : INotificationService
{
    public Task PublishAsync(string title, string body, CancellationToken cancellationToken)
    {
        return PublishAsync(
            new DesktopNotificationRequest(Guid.NewGuid().ToString("N"), title, body),
            cancellationToken);
    }

    public async Task PublishAsync(DesktopNotificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("UserNotifications requires macOS.");
        }

        var identifier = string.IsNullOrWhiteSpace(request.Id)
            ? Guid.NewGuid().ToString("N")
            : request.Id;
        var title = request.Title ?? "";
        var body = request.Body ?? "";

        int result;
        try
        {
            result = MacNative.PublishNotification(
                identifier,
                title,
                body,
                request.ActivationUri ?? "");
        }
        catch (DllNotFoundException)
        {
            // Every host carries its own copy of libMptMacNative.dylib next to its apphost,
            // so a packaging slip can leave one of them without the library. A banner through
            // osascript is worth more than a lost notification.
            result = MacNative.NotificationUnavailable;
        }
        catch (EntryPointNotFoundException)
        {
            result = MacNative.NotificationUnavailable;
        }

        if (result == 0)
        {
            return;
        }

        // UNUserNotificationCenter is only reachable from a process whose main bundle carries
        // an identifier. The shipped hosts run from nested helper bundles
        // (Contents/MacOS/Helpers/MyPowerTools Runner.app and friends) and take that path,
        // which is what keeps the notification clickable: the native delegate opens the
        // activation URI through mypowertools://. Development runs and any host started
        // outside a bundle still have no identifier, so the native layer reports the
        // condition instead of raising an Objective-C exception and osascript delivers a
        // plain banner without click-through.
        if (result is MacNative.NotificationUnavailable
            or MacNative.NotificationNoBundle
            or MacNative.NotificationOsUnsupported)
        {
            await PublishThroughOsascriptAsync(title, body, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException(
            $"UserNotifications rejected the notification request ({result}).");
    }

    private static async Task PublishThroughOsascriptAsync(
        string title,
        string body,
        CancellationToken cancellationToken)
    {
        var script = $"display notification {EscapeAppleScriptString(body)} with title {EscapeAppleScriptString(title)}";
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start osascript to publish the notification.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException(
                error.Length > 0
                    ? $"osascript could not publish the notification: {error}"
                    : $"osascript could not publish the notification (exit code {process.ExitCode}).");
        }
    }

    internal static string EscapeAppleScriptString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
