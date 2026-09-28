using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless;
using MyPowerTools.Android.Input;

namespace MyPowerTools.Android.Tests;

[Collection("Android Avalonia host")]
public sealed class AndroidImeHostTests
{
    [Theory]
    [InlineData(true, 280, 320)]
    [InlineData(true, 180, 420)]
    [InlineData(false, 280, 600)]
    public async Task Keyboard_reduces_content_without_replacing_system_bar_safe_area(
        bool edgeToEdge, double keyboardHeight, double expectedContentHeight)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TouchTestAppBuilder));
        await session.Dispatch(() =>
        {
            var composer = new Button { Content = "发送", Height = 48 };
            var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            content.Children.Add(new ScrollViewer { Content = new TextBlock { Text = "会话" } });
            Grid.SetRow(composer, 1);
            content.Children.Add(composer);
            // The root's padding belongs to Avalonia's system-bar safe area, not the IME.
            var host = new AndroidImeHost(content) { Padding = new Thickness(0, 24, 0, 16) };
            var window = new Window { Width = 390, Height = 640, Content = host };
            window.Show();
            window.UpdateLayout();
            host.ApplyInputPane(edgeToEdge, InputPaneState.Open, new Rect(0, 344, 390, keyboardHeight));
            window.UpdateLayout();
            Assert.Equal(new Thickness(0, 24, 0, 16), host.Padding);
            Assert.Equal(expectedContentHeight, content.Bounds.Height);
            Assert.Equal(24, content.TranslatePoint(default, window)!.Value.Y);
            Assert.Equal(24 + expectedContentHeight,
                composer.TranslatePoint(new Point(0, composer.Bounds.Height), window)!.Value.Y);

            host.ApplyInputPane(edgeToEdge, InputPaneState.Closed, default);
            window.UpdateLayout();
            Assert.Equal(600, content.Bounds.Height);
            window.Close();
        }, CancellationToken.None);
    }
}
