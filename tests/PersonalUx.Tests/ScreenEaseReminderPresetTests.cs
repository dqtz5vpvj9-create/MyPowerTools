using ScreenEase.Surface.ViewModels;

namespace MyPowerTools.Tests;

public sealed class ScreenEaseReminderPresetTests
{
    [Fact]
    public void Built_in_presets_cover_short_and_long_focus_sessions()
    {
        Assert.Collection(
            ScreenEaseReminderPreset.BuiltIns,
            preset =>
            {
                Assert.Equal("pomodoro", preset.Id);
                Assert.Equal((25, 5, 15, 4),
                    (preset.FocusMinutes, preset.ShortBreakMinutes, preset.LongBreakMinutes, preset.LongBreakInterval));
            },
            preset =>
            {
                Assert.Equal("deep-work", preset.Id);
                Assert.Equal((50, 10, 20, 2),
                    (preset.FocusMinutes, preset.ShortBreakMinutes, preset.LongBreakMinutes, preset.LongBreakInterval));
            });
    }
}
