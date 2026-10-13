using InputMonitor.Core;
using Xunit;

namespace MyPowerTools.Tests;

public sealed class InputMonitorRestTimerTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Work(FatigueEngine engine, Clock clock, int seconds)
    {
        engine.NotifyActivity(FatigueActivitySource.Keyboard);
        for (var i = 0; i < seconds; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            engine.NotifyActivity(FatigueActivitySource.Keyboard);
            engine.Tick(clock.Now);
        }
    }

    [Fact]
    public void Full_interval_reminds_once_and_skip_requires_another_full_interval()
    {
        var clock = new Clock();
        var engine = new FatigueEngine(new() { RemindIntervalMinutes = 5 }, clock);
        var reminders = 0;
        engine.OnShouldRemind = () => reminders++;
        Work(engine, clock, 299);
        Assert.Equal(0, reminders);
        Work(engine, clock, 1);
        Assert.Equal(1, reminders);
        Work(engine, clock, 30);
        Assert.Equal(1, reminders);
        engine.Skip();
        Assert.Equal(0, engine.Value);
        Work(engine, clock, 299);
        Assert.Equal(1, reminders);
        Work(engine, clock, 1);
        Assert.Equal(2, reminders);
        engine.RestDone();
        Assert.Equal(0, engine.Value);
    }

    [Fact]
    public void Idle_tail_is_refunded_and_five_minute_break_clears_progress()
    {
        var clock = new Clock();
        var engine = new FatigueEngine(new(), clock);
        Work(engine, clock, 120);
        var before = engine.Value;
        for (var i = 0; i < 60; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            engine.Tick(clock.Now);
        }
        Assert.Equal(before, engine.Value, 6);
        Work(engine, clock, 10);
        Assert.True(engine.Value > before);
        clock.Now = clock.Now.AddMinutes(5);
        // Input arriving before a timer callback must still credit the break.
        engine.NotifyActivity(FatigueActivitySource.Mouse);
        Assert.Equal(0, engine.Value);
    }

    [Fact]
    public void App_heartbeats_never_drive_work_even_with_legacy_setting_enabled()
    {
        var clock = new Clock();
        var engine = new FatigueEngine(new() { FatigueFromApp = true }, clock);
        for (var i = 0; i < 1500; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            engine.NotifyActivity(FatigueActivitySource.App);
            engine.Tick(clock.Now);
        }
        Assert.Equal(0, engine.Value);
    }

    [Fact]
    public void Pause_freezes_timer_and_resume_starts_fresh_without_reminding()
    {
        var clock = new Clock();
        var engine = new FatigueEngine(new() { RemindAfterResume = true }, clock);
        Work(engine, clock, 100);
        engine.SetPaused(true);
        var before = engine.Value;
        Work(engine, clock, 1500);
        Assert.Equal(before, engine.Value);
        var reminded = false;
        engine.OnShouldRemind = () => reminded = true;
        engine.SetPaused(false);
        Assert.Equal(0, engine.Value);
        Assert.False(reminded);
    }

    [Fact]
    public void Restart_keeps_short_break_progress_and_clears_long_break_or_legacy_data()
    {
        var clock = new Clock();
        var settings = new MonitorSettings();
        var engine = new FatigueEngine(settings, clock);
        Work(engine, clock, 120);
        var before = engine.Value;
        clock.Now = clock.Now.AddSeconds(30);
        engine = new FatigueEngine(settings.Clone(), clock);
        Assert.Equal(before, engine.Value, 6);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Equal(0, new FatigueEngine(settings.Clone(), clock).Value);
        Assert.Equal(0, new FatigueEngine(new() { FatigueValue = 99 }, clock).Value);
    }

    [Fact]
    public void Lock_stops_immediately_and_sleep_never_adds_work()
    {
        var clock = new Clock();
        var engine = new FatigueEngine(new(), clock);
        Work(engine, clock, 100);
        var before = engine.Value;
        clock.Now = clock.Now.AddSeconds(1);
        engine.Tick(clock.Now, screenLocked: true);
        clock.Now = clock.Now.AddSeconds(1);
        engine.Tick(clock.Now);
        Assert.Equal(before, engine.Value, 6);
        clock.Now = clock.Now.AddMinutes(5);
        engine.Tick(clock.Now);
        Assert.Equal(0, engine.Value);
    }

    [Fact]
    public void Manual_rest_skip_also_starts_fresh()
    {
        var clock = new Clock();
        var engine = new FatigueEngine(new(), clock);
        Work(engine, clock, 100);
        engine.ManualRest();
        engine.Skip();
        Assert.Equal(0, engine.Value);
        Assert.False(engine.IsResting);
    }
}
