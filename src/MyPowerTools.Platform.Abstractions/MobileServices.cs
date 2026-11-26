namespace MyPowerTools.Platform.Abstractions;

/// <summary>Platform-owned lifetime for work that must continue while its tool UI is hidden.</summary>
public interface IBackgroundActivityService
{
    Task<IDisposable> BeginAsync(string moduleId, string title, bool waitingForPeers, CancellationToken cancellationToken);
}

/// <summary>Publishes a completed private file into the user's Downloads collection.</summary>
public interface IDownloadsService
{
    Task PublishAsync(string path, CancellationToken cancellationToken);
}
