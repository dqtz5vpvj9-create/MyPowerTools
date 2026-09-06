using System.Diagnostics;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacUserNotificationService : INotificationService
{
    public string GetAuthorizationStatus()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return "unavailable";
        }

        try
        {
            return MacNative.GetNotificationAuthorizationStatus() switch
            {
                MacNative.NotificationAuthorizationNotDetermined => "not-determined",
                MacNative.NotificationAuthorizationDenied => "denied",
                MacNative.NotificationAuthorizationAuthorized => "authorized",
                MacNative.NotificationAuthorizationProvisional => "provisional",
                _ => "unavailable"
            };
        }
        catch (DllNotFoundException)
        {
            return "unavailable";
        }
        catch (EntryPointNotFoundException)
        {
            return "unavailable";
        }
    }

    public Task PublishAsync(string title, string body, CancellationToken cancellationToken)
    {
        return PublishAsync(
            new DesktopNotificationRequest(Guid.NewGuid().ToString("N"), title, body),
            cancellationToken);
    }

    public async Task PublishAsync(
        DesktopNotificationRequest request,
        CancellationToken cancellationToken)
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

        if (result == MacNative.NotificationPermissionDenied)
        {
            throw new UnauthorizedAccessException(
                "macOS notification permission is disabled for MyPowerTools. " +
                "Enable notifications in System Settings > Notifications > MyPowerTools Remote Notifications.");
        }

        if (result == MacNative.NotificationDeliveryFailed)
        {
            throw new InvalidOperationException(
                "UserNotifications rejected the notification before it entered Notification Center.");
        }

        if (result == MacNative.NotificationTimedOut)
        {
            throw new InvalidOperationException(
                "UserNotifications did not complete authorization or delivery within the native timeout.");
        }

        if (result is MacNative.NotificationUnavailable
            or MacNative.NotificationNoBundle
            or MacNative.NotificationOsUnsupported)
        {
            if (AllowUnidentifiedDevelopmentFallback())
            {
                await PublishThroughOsascriptAsync(title, body, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var detail = result switch
            {
                MacNative.NotificationNoBundle =>
                    "The publishing process has no macOS application bundle identity.",
                MacNative.NotificationUnavailable =>
                    "The native macOS notification library is unavailable.",
                _ => "The running macOS version does not support UserNotifications."
            };
            throw new InvalidOperationException(
                $"{detail} A production notification cannot fall back to a non-clickable osascript banner.");
        }

        throw new InvalidOperationException(
            $"UserNotifications rejected the notification request ({result}).");
    }

    private static bool AllowUnidentifiedDevelopmentFallback()
    {
        return Debugger.IsAttached || string.Equals(
            Environment.GetEnvironmentVariable(
                "MPT_ALLOW_UNIDENTIFIED_NOTIFICATION_FALLBACK"),
            "1",
            StringComparison.Ordinal);
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
            ?? throw new InvalidOperationException(
                "Could not start osascript to publish the notification.");
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
