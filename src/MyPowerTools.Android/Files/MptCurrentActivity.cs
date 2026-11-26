using A = global::Android;

namespace MyPowerTools.Android.Files;

/// <summary>
/// Tracks the resumed activity so a viewer opens on top of the MyPowerTools task and Back returns to
/// the app the user came from, instead of a detached task. This feature registers the callback
/// itself, so the host activity is untouched; when nothing is resumed the launcher falls back to the
/// application context with <c>FLAG_ACTIVITY_NEW_TASK</c>.
/// </summary>
internal sealed class MptCurrentActivity : Java.Lang.Object, A.App.Application.IActivityLifecycleCallbacks
{
    private static readonly object Gate = new();
    private static MptCurrentActivity? _tracker;

    private WeakReference<A.App.Activity>? _current;

    /// <summary>The resumed activity, or <see langword="null"/> when none can host the viewer.</summary>
    internal static A.App.Activity? Current
    {
        get
        {
            if (Volatile.Read(ref _tracker)?._current is not { } reference ||
                !reference.TryGetTarget(out var activity))
            {
                return null;
            }

            // An activity that is finishing or already destroyed must not receive the intent.
            return activity.IsFinishing || activity.IsDestroyed ? null : activity;
        }
    }

    /// <summary>Registers the tracker once, if the process context really is the application object.</summary>
    internal static void EnsureRegistered(A.Content.Context context)
    {
        if (Volatile.Read(ref _tracker) is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_tracker is not null)
            {
                return;
            }

            if (context is not A.App.Application application)
            {
                // No lifecycle source: the launcher's application-context fallback still works.
                return;
            }

            var tracker = new MptCurrentActivity();
            application.RegisterActivityLifecycleCallbacks(tracker);
            Volatile.Write(ref _tracker, tracker);
        }
    }

    public void OnActivityCreated(A.App.Activity? activity, A.OS.Bundle? savedInstanceState)
    {
    }

    public void OnActivityStarted(A.App.Activity? activity)
    {
    }

    public void OnActivityResumed(A.App.Activity? activity)
    {
        if (activity is not null)
        {
            _current = new WeakReference<A.App.Activity>(activity);
        }
    }

    public void OnActivityPaused(A.App.Activity? activity) => Clear(activity);

    public void OnActivityStopped(A.App.Activity? activity)
    {
    }

    public void OnActivitySaveInstanceState(A.App.Activity? activity, A.OS.Bundle? outState)
    {
    }

    public void OnActivityDestroyed(A.App.Activity? activity) => Clear(activity);

    private void Clear(A.App.Activity? activity)
    {
        if (activity is not null &&
            _current is { } reference &&
            reference.TryGetTarget(out var current) &&
            ReferenceEquals(current, activity))
        {
            _current = null;
        }
    }
}
