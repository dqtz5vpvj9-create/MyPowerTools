namespace InputMonitor.Core;

/// <summary>
/// Distance/time dual-threshold sampler for cursor polling.
/// Matches the macOS EventSampler: 30px or 50ms, ignore &lt; 0.5px idle jitter.
/// </summary>
public sealed class EventSampler
{
    public double MinDistance { get; set; }
    public ulong MinIntervalNs { get; set; }

    private double? _lastSampleX;
    private double? _lastSampleY;
    private ulong _lastSampleTs;
    private double? _lastRawX;
    private double? _lastRawY;
    private double _pendingDelta;

    public EventSampler(double minDistance = 30, ulong minIntervalNs = 50_000_000)
    {
        MinDistance = minDistance;
        MinIntervalNs = minIntervalNs;
    }

    public (bool Sampled, double MoveDelta) Feed(double x, double y, ulong timestampNs)
    {
        if (_lastRawX is { } lx && _lastRawY is { } ly)
        {
            var dx = x - lx;
            var dy = y - ly;
            _pendingDelta += Math.Sqrt((dx * dx) + (dy * dy));
        }

        _lastRawX = x;
        _lastRawY = y;

        if (_lastSampleX is not { } sx || _lastSampleY is not { } sy)
        {
            _lastSampleX = x;
            _lastSampleY = y;
            _lastSampleTs = timestampNs;
            var first = _pendingDelta;
            _pendingDelta = 0;
            return (true, first);
        }

        if (_pendingDelta < 0.5)
        {
            return (false, 0);
        }

        var distX = x - sx;
        var distY = y - sy;
        var distFromSample = Math.Sqrt((distX * distX) + (distY * distY));
        var elapsed = timestampNs - _lastSampleTs;
        if (distFromSample >= MinDistance || elapsed >= MinIntervalNs)
        {
            _lastSampleX = x;
            _lastSampleY = y;
            _lastSampleTs = timestampNs;
            var delta = _pendingDelta;
            _pendingDelta = 0;
            return (true, delta);
        }

        return (false, 0);
    }

    public void Reset()
    {
        _lastSampleX = null;
        _lastSampleY = null;
        _lastSampleTs = 0;
        _lastRawX = null;
        _lastRawY = null;
        _pendingDelta = 0;
    }
}
