using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.VisualTree;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>Remove hidden progress animations from the clock without losing their bindings.</summary>
internal static class MacIdleProgressAnimations
{
    private static readonly ConditionalWeakTable<ProgressBar, Suspension> States = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed || !OperatingSystem.IsMacOS()) return;
        _installed = true;
        ProgressBar.IsIndeterminateProperty.Changed.AddClassHandler<ProgressBar>((bar, _) =>
            States.GetValue(bar, static control => new Suspension(control)).Update());
    }

    private sealed class Suspension
    {
        private readonly ProgressBar _bar;
        private IDisposable? _override;
        private bool _updating;
        private readonly List<Visual> _ancestors = new();

        public Suspension(ProgressBar bar)
        {
            _bar = bar;
            bar.PropertyChanged += OnVisibilityChanged;
            bar.AttachedToVisualTree += (_, _) => { ObserveAncestors(); Update(); };
            bar.DetachedFromVisualTree += (_, _) => { ClearAncestors(); Suspend(); };
            ObserveAncestors();
        }

        private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty || args.Property == Window.WindowStateProperty)
                Update();
        }

        private void ObserveAncestors()
        {
            ClearAncestors();
            foreach (var ancestor in _bar.GetVisualAncestors())
            {
                _ancestors.Add(ancestor);
                ancestor.PropertyChanged += OnVisibilityChanged;
            }
        }

        private void ClearAncestors()
        {
            foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnVisibilityChanged;
            _ancestors.Clear();
        }

        public void Update()
        {
            if (_updating) return;
            if (!_bar.IsEffectivelyVisible || TopLevel.GetTopLevel(_bar) is not { } root ||
                root is Window { WindowState: WindowState.Minimized })
            {
                Suspend();
                return;
            }

            _updating = true;
            try
            {
                _override?.Dispose();
                _override = null;
            }
            finally { _updating = false; }
        }

        private void Suspend()
        {
            if (_updating || _override is not null) return;
            _updating = true;
            try
            {
                // Animation priority is temporary; the original local value or
                // binding keeps receiving updates and is restored when shown.
                _override = _bar.SetValue(ProgressBar.IsIndeterminateProperty, false, BindingPriority.Animation);
            }
            finally { _updating = false; }
        }
    }
}
