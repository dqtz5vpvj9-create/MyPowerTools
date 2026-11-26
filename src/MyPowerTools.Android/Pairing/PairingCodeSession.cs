namespace MyPowerTools.Android.Pairing;

/// <summary>
/// The camera start/stop state machine for the QR viewfinder, with no Android types in it so the
/// rules are unit tested on a build machine.
/// <para>
/// Android delivers <c>SurfaceCreated</c>, <c>SurfaceChanged</c>, <c>OnResume</c> and
/// <c>CameraDevice.StateCallback.OnOpened</c> on different threads and in an order the app does not
/// control, and <c>SurfaceChanged</c> alone fires repeatedly. Every start is a numbered attempt:
/// <see cref="TryBeginOpen"/> hands out the number, <see cref="MarkOpened"/> only accepts an answer
/// for the attempt that is still current, and pausing, swapping the surface or destroying the
/// session abandons the current attempt. That is what stops a duplicate <c>openCamera</c>, a camera
/// reopened after <c>OnPause</c>, an answer delivered to a restarted attempt, and a session that was
/// configured after the scan ended.
/// </para>
/// </summary>
public sealed class PairingCodeSession
{
    private bool _closed;
    private bool _resumed;
    private bool _surfaceReady;
    private bool _openInFlight;
    private long _attemptCounter;
    private long? _currentAttempt;

    /// <summary>A camera was accepted and has not been closed yet.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>True once <see cref="TryBeginOpen"/> started a camera that has not answered yet.</summary>
    public bool IsOpening => _openInFlight;

    /// <summary>True while this session may still use a camera at all.</summary>
    public bool IsActive => !_closed;

    /// <summary>
    /// Swaps the surface while keeping the session: a new surface always abandons the attempt bound
    /// to the old one.
    /// </summary>
    public void OnSurfaceReady(bool surfaceIsValid)
    {
        _surfaceReady = surfaceIsValid;
        if (surfaceIsValid)
        {
            AbandonOpen();
        }
    }

    public void OnSurfaceDestroyed()
    {
        _surfaceReady = false;
        AbandonOpen();
    }

    /// <summary>Resuming after the camera was released has to open it again.</summary>
    public void OnResumed()
    {
        _resumed = true;
        AbandonOpen();
    }

    public void OnPaused()
    {
        _resumed = false;
        AbandonOpen();
    }

    /// <summary>
    /// Claims the right to call <c>openCamera</c> and returns the attempt number to pass back to
    /// <see cref="MarkOpened"/>. Returns <see langword="null"/> when the session is closed, not
    /// resumed, has no usable surface, already has a camera, or already has an open attempt in
    /// flight.
    /// </summary>
    public long? TryBeginOpen()
    {
        if (_closed || !_resumed || !_surfaceReady || IsOpen || _openInFlight)
        {
            return null;
        }

        _openInFlight = true;
        _currentAttempt = ++_attemptCounter;
        return _currentAttempt;
    }

    /// <summary>True when <paramref name="attemptId"/> is the start request that is still awaited.</summary>
    public bool IsCurrentAttempt(long attemptId) =>
        !_closed && _openInFlight && _currentAttempt == attemptId;

    /// <summary>
    /// Reports the camera the device handed back for <paramref name="attemptId"/>. Returns
    /// <see langword="false"/> when that attempt was abandoned (paused, surface swapped, destroyed)
    /// or the session closed, which means the caller must close the camera it received instead of
    /// attaching a preview to a screen the user already left. A stale attempt leaves the current one
    /// untouched.
    /// </summary>
    public bool MarkOpened(long attemptId)
    {
        if (!IsCurrentAttempt(attemptId))
        {
            return false;
        }

        _openInFlight = false;
        _currentAttempt = null;
        IsOpen = true;
        return true;
    }

    /// <summary>
    /// The current attempt failed, disconnected or its camera was closed; a later surface event may
    /// open one again. Ignored when the attempt is stale, so it cannot clear a newer attempt.
    /// </summary>
    public void MarkClosed(long attemptId)
    {
        if (!IsCurrentAttempt(attemptId))
        {
            return;
        }

        _openInFlight = false;
        _currentAttempt = null;
        IsOpen = false;
    }

    /// <summary>
    /// Reports that the open camera is gone without claiming an attempt (the activity closed it
    /// deliberately in <c>OnPause</c> or <c>OnDestroy</c>).
    /// </summary>
    public void MarkCameraLost()
    {
        IsOpen = false;
    }

    /// <summary>
    /// Ends the session. After this every <see cref="TryBeginOpen"/> is refused, so a callback that
    /// arrives after the user finished (or after the activity is gone) cannot reopen the camera or
    /// complete a different request.
    /// </summary>
    public void Close()
    {
        _closed = true;
        _resumed = false;
        _surfaceReady = false;
        IsOpen = false;
        _openInFlight = false;
        _currentAttempt = null;
    }

    /// <summary>
    /// Gives up the start request that has not answered yet. The attempt number is left behind, so an
    /// answer for it is refused while a later attempt is free to open normally.
    /// </summary>
    private void AbandonOpen()
    {
        _openInFlight = false;
        _currentAttempt = null;
        IsOpen = false;
    }
}
