using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;

namespace MyPowerTools.Android.Input;

/// <summary>
/// Gives every mobile tool the space above the keyboard. Avalonia applies system-bar safe area
/// to this root separately; its Android InputPane rectangle already excludes the navigation bar.
/// Legacy fitted windows resize natively, so only edge-to-edge windows need this extra inset.
/// </summary>
internal sealed class AndroidImeHost : UserControl
{
    private readonly Border _viewport;

    internal AndroidImeHost(Control child)
    {
        _viewport = new Border { Child = child };
        Content = _viewport;
    }

    private TopLevel? _topLevel;
    private IInputPane? _inputPane;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _inputPane = _topLevel?.InputPane;
        if (_inputPane is not null)
        {
            _inputPane.StateChanged += OnInputPaneChanged;
            RefreshInputPane();
        }
    }

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e) =>
        ApplyInputPane(_topLevel?.InsetsManager?.DisplaysEdgeToEdge == true, e.NewState, e.EndRect);

    // On newer Android versions Avalonia raises StateChanged from the IME animation callback.
    // A native insets change without an animation (for example enabling the soft keyboard while
    // a hardware keyboard and a focused editor remain active) still updates the pane's properties.
    internal void RefreshInputPane()
    {
        if (_inputPane is not null)
            RefreshInputPane(_topLevel?.InsetsManager?.DisplaysEdgeToEdge == true, _inputPane);
    }

    internal void RefreshInputPane(bool edgeToEdge, IInputPane inputPane) =>
        ApplyInputPane(edgeToEdge, inputPane.State, inputPane.OccludedRect);

    internal void ApplyInputPane(bool edgeToEdge, InputPaneState state, Rect occludedRect)
    {
        // Use the animation's destination, rather than the old rectangle reported at its start.
        // Padding relays the new available height through the ordinary shared layout pipeline.
        _viewport.Padding = new Thickness(0, 0, 0,
            edgeToEdge && state == InputPaneState.Open ? occludedRect.Height : 0);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_inputPane is not null) _inputPane.StateChanged -= OnInputPaneChanged;
        _inputPane = null;
        _topLevel = null;
        _viewport.Padding = default;
        base.OnDetachedFromVisualTree(e);
    }
}
