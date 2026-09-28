using MyPowerTools.Android.Pairing;

namespace MyPowerTools.Android.Tests;

/// <summary>
/// The camera start/stop rules. Android raises SurfaceCreated/SurfaceChanged/OnResume/OnOpened on
/// different threads and far more often than the camera should actually open, so this is where a
/// duplicate <c>openCamera</c>, a camera reopened after OnPause, or a session configured after the
/// scan ended would show up.
/// </summary>
public sealed class PairingCodeSessionTests
{
    private static PairingCodeSession Ready()
    {
        var session = new PairingCodeSession();
        session.OnSurfaceReady(true);
        session.OnResumed();
        return session;
    }

    /// <summary>A started attempt, or a failed assertion when the session refused to start one.</summary>
    private static long Started(PairingCodeSession session)
    {
        var attempt = session.TryBeginOpen();
        Assert.NotNull(attempt);
        return attempt!.Value;
    }

    [Fact]
    public void A_ready_session_opens_exactly_once_until_the_camera_answers()
    {
        var session = Ready();

        var attempt = Started(session);
        Assert.True(session.IsOpening);

        // SurfaceChanged fires repeatedly while the activity comes up; none of them may queue a
        // second openCamera call on top of the one that has not answered yet.
        Assert.Null(session.TryBeginOpen());
        Assert.Null(session.TryBeginOpen());

        Assert.True(session.MarkOpened(attempt));
        Assert.False(session.IsOpening);
        Assert.True(session.IsOpen);

        // The camera is already open, so a later surface change must not open a second one.
        Assert.Null(session.TryBeginOpen());
    }

    [Fact]
    public void A_session_without_a_surface_or_without_resume_refuses_to_open()
    {
        var session = new PairingCodeSession();
        Assert.Null(session.TryBeginOpen());

        session.OnSurfaceReady(true);
        Assert.Null(session.TryBeginOpen());

        session.OnResumed();
        Assert.NotNull(session.TryBeginOpen());
    }

    [Fact]
    public void A_new_surface_replaces_the_camera_bound_to_the_old_one()
    {
        var session = Ready();
        Assert.True(session.MarkOpened(Started(session)));

        session.OnSurfaceReady(true);

        Assert.False(session.IsOpen);
        Assert.NotNull(session.TryBeginOpen());
    }

    [Fact]
    public void Pausing_closes_the_session_so_a_late_camera_callback_cannot_preview()
    {
        var session = Ready();
        var attempt = Started(session);

        session.OnPaused();

        // The camera answered after the activity was paused: the caller must close it instead of
        // attaching a preview to a screen the user already left.
        Assert.False(session.MarkOpened(attempt));
        Assert.False(session.IsOpen);
        Assert.Null(session.TryBeginOpen());
    }

    [Fact]
    public void Completing_the_scan_refuses_every_later_open()
    {
        var session = Ready();
        Assert.True(session.MarkOpened(Started(session)));

        session.Close();

        Assert.False(session.IsActive);
        Assert.False(session.IsOpen);
        Assert.False(session.IsOpening);
        Assert.Null(session.TryBeginOpen());

        // OnResume can still arrive after the user finished (the activity is on its way out).
        session.OnResumed();
        session.OnSurfaceReady(true);
        Assert.Null(session.TryBeginOpen());
    }

    [Fact]
    public void A_camera_that_answers_after_the_session_closed_is_refused()
    {
        var session = Ready();
        var attempt = Started(session);

        session.Close();

        Assert.False(session.MarkOpened(attempt));
        Assert.False(session.IsOpen);
    }

    [Fact]
    public void Resuming_opens_a_new_camera_after_the_previous_one_was_closed()
    {
        var session = Ready();
        Assert.True(session.MarkOpened(Started(session)));

        session.OnPaused();
        session.OnResumed();
        session.OnSurfaceReady(true);

        Assert.NotNull(session.TryBeginOpen());
    }

    [Fact]
    public void A_camera_that_answers_after_the_surface_was_destroyed_is_refused()
    {
        var session = Ready();
        var attempt = Started(session);

        session.OnSurfaceDestroyed();

        Assert.False(session.IsOpening);
        Assert.False(session.MarkOpened(attempt));
        Assert.Null(session.TryBeginOpen());
    }

    [Fact]
    public void A_camera_that_answers_after_a_resume_cycle_was_restarted_is_refused()
    {
        // Pause abandoned the first attempt; the resume started a second one. The answer to the
        // first request must not be mistaken for the answer to the second.
        var session = Ready();
        var abandoned = Started(session);
        session.OnPaused();
        session.OnResumed();
        var current = Started(session);

        Assert.False(session.MarkOpened(abandoned));
        Assert.True(session.IsOpening);
        Assert.True(session.IsCurrentAttempt(current));
        Assert.True(session.MarkOpened(current));
        Assert.True(session.IsOpen);
    }

    [Fact]
    public void A_stale_failure_does_not_clear_the_current_attempt()
    {
        var session = Ready();
        var abandoned = Started(session);
        session.OnPaused();
        session.OnResumed();
        _ = Started(session);

        session.MarkClosed(abandoned);

        Assert.True(session.IsOpening);
        Assert.Null(session.TryBeginOpen());
    }

    [Fact]
    public void A_failed_open_can_be_retried_by_the_next_surface_event()
    {
        var session = Ready();
        var attempt = Started(session);

        session.MarkClosed(attempt);

        Assert.False(session.IsOpen);
        Assert.False(session.IsOpening);
        Assert.NotNull(session.TryBeginOpen());
    }

    [Fact]
    public void An_invalid_surface_never_counts_as_ready()
    {
        var session = Ready();
        session.OnSurfaceReady(false);

        Assert.Null(session.TryBeginOpen());
    }
}
