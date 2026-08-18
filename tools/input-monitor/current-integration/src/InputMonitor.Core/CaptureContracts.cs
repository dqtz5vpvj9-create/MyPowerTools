namespace InputMonitor.Core;

public interface IInputCapture : IDisposable
{
    event Action<InputEventRecord>? EventReceived;
    bool IsRunning { get; }
    void Start();
    void Stop();
    void UpdateTrackSampleDistance(double pixels);
}

public interface IFrontAppTracker : IDisposable
{
    event Action<FrontAppSession>? SessionCompleted;
    event Action<FrontAppSession, DateTimeOffset>? Heartbeat;
    bool ScreenLocked { get; }
    FrontAppSession? CurrentSession { get; }
    void Start();
    void Stop();
    void UpdateHeartbeatInterval(TimeSpan interval);
}

public interface IRestOverlay
{
    bool IsShowing { get; }
    void Show(int totalSeconds, Action skip, Action finished);
    void Dismiss();
}

public interface IInteractionBaseline
{
    IReadOnlyCollection<int> LoadTodayMinutes();
}
