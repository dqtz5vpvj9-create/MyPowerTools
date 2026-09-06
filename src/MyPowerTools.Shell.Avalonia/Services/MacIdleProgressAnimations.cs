using Avalonia;
using Avalonia.Animation;
using Avalonia.Data;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>The battery-oriented macOS edition applies decorative transitions immediately.</summary>
internal static class MacIdleProgressAnimations
{
    private static bool _installed;
    public static void Install()
    {
        if (_installed || !OperatingSystem.IsMacOS()) return;
        _installed = true;
        Animatable.TransitionsProperty.Changed.AddClassHandler<Animatable>((control, _) =>
        {
            if (control.Transitions is not null)
                control.SetValue(Animatable.TransitionsProperty, null, BindingPriority.Animation);
        });
    }
}
