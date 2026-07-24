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

    public Task PublishAsync(DesktopNotificationRequest request, CancellationToken cancellationToken)
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
        try
        {
            var result = MacNative.PublishNotification(
                identifier,
                request.Title ?? "",
                request.Body ?? "",
                request.ActivationUri ?? "");
            if (result != 0)
            {
                throw new InvalidOperationException($"UserNotifications rejected the notification request ({result}).");
            }
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "The macOS native capability library is missing from the application bundle.",
                ex);
        }

        return Task.CompletedTask;
    }
}
