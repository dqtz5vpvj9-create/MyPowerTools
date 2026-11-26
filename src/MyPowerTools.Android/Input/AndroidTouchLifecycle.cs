using Android.Views;
using Avalonia.Android;

namespace MyPowerTools.Android.Input;

/// <summary>Ends interrupted native touch sequences before another activity takes the window.</summary>
internal sealed class AndroidTouchLifecycle : IDisposable
{
    private readonly ActiveTouchSequences _contacts = new();
    private MotionEvent? _lastEvent;

    public void BeforeDispatch(MotionEvent e)
    {
        if ((e.ActionMasked is MotionEventActions.Down or MotionEventActions.PointerDown) &&
            e.GetToolType(e.ActionIndex) == MotionEventToolType.Finger)
            _contacts.Begin(e.GetPointerId(e.ActionIndex));

        if (_contacts.Count == 0) return;
        var previous = _lastEvent;
        _lastEvent = MotionEvent.Obtain(e);
        previous?.Dispose();
    }

    public void AfterDispatch(MotionEvent e)
    {
        if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.PointerUp)
            _contacts.End(e.GetPointerId(e.ActionIndex));
        if (_contacts.Count == 0)
        {
            _lastEvent?.Dispose();
            _lastEvent = null;
        }
    }

    public bool Cancel(AvaloniaView? view, string reason)
    {
        var contacts = _contacts.TakeForCancellation();
        using var lastEvent = _lastEvent;
        _lastEvent = null;
        if (view is null || lastEvent is null || contacts.Length == 0) return false;

        foreach (var pointerId in contacts)
        {
            var index = lastEvent.FindPointerIndex(pointerId);
            if (index < 0) continue;
            using var cancel = MotionEvent.Obtain(lastEvent)!;
            // Avalonia 12.0.5 translates CANCEL only for ActionIndex. Send one for every active
            // finger so a two-finger gesture cannot keep its second capture after a viewer opens.
            cancel.Action = (MotionEventActions)((int)MotionEventActions.Cancel | (index << 8));
            view.DispatchTouchEvent(cancel);
        }
        AndroidStartupLog.Info("touch-lifecycle", $"Cancelled {contacts.Length} active contact(s): {reason}");
        return true;
    }

    public void Dispose()
    {
        _contacts.TakeForCancellation();
        _lastEvent?.Dispose();
        _lastEvent = null;
    }
}
