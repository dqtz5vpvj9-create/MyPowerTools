using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MyPowerTools.AvaloniaSdk;

/// <summary>
/// Opt-in responsive layout for forms, toolbars and master/detail sections. At the declared
/// width, grid cells become reading-order rows and horizontal stacks become vertical.
/// Original definitions and cell spans are retained so tablet/desktop resizing is reversible.
/// </summary>
public sealed class MptAdaptiveLayout : AvaloniaObject
{
    public static readonly AttachedProperty<double> StackBelowProperty =
        AvaloniaProperty.RegisterAttached<MptAdaptiveLayout, Control, double>("StackBelow", 0);
    public static readonly AttachedProperty<string> ColumnLabelsProperty =
        AvaloniaProperty.RegisterAttached<MptAdaptiveLayout, Grid, string>("ColumnLabels", "");
    public static readonly AttachedProperty<double> HideBelowProperty =
        AvaloniaProperty.RegisterAttached<MptAdaptiveLayout, Control, double>("HideBelow", 0);
    private static readonly ConditionalWeakTable<Control, LayoutState> States = new();
    private static readonly ConditionalWeakTable<Control, HideState> HideStates = new();

    static MptAdaptiveLayout()
    {
        StackBelowProperty.Changed.AddClassHandler<Control>((control, _) =>
            States.GetValue(control, c => new LayoutState(c)).RequestUpdate());
        HideBelowProperty.Changed.AddClassHandler<Control>((control, _) =>
            HideStates.GetValue(control, c => new HideState(c)).Refresh());
    }

    public static void SetStackBelow(Control control, double width) => control.SetValue(StackBelowProperty, width);
    public static double GetStackBelow(Control control) => control.GetValue(StackBelowProperty);

    public static void SetColumnLabels(Grid grid, string labels) => grid.SetValue(ColumnLabelsProperty, labels);
    public static string GetColumnLabels(Grid grid) => grid.GetValue(ColumnLabelsProperty);
    public static void SetHideBelow(Control control, double width) => control.SetValue(HideBelowProperty, width);
    public static double GetHideBelow(Control control) => control.GetValue(HideBelowProperty);

    private sealed class HideState
    {
        private readonly Control _control;
        private Control? _parent;
        private bool _pending;

        public HideState(Control control)
        {
            _control = control;
            _control.AttachedToVisualTree += OnAttachedToVisualTree;
            _control.DetachedFromVisualTree += OnDetachedFromVisualTree;

            if (_control.IsAttachedToVisualTree())
            {
                AttachToParent();
            }
        }

        public void Refresh()
        {
            if (_control.IsAttachedToVisualTree())
            {
                AttachToParent();
                Schedule();
            }
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            AttachToParent();
            Schedule();
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            DetachFromParent();
        }

        private void AttachToParent()
        {
            var nextParent = _control.Parent as Control;
            if (ReferenceEquals(_parent, nextParent))
            {
                return;
            }

            DetachFromParent();
            _parent = nextParent;
            if (_parent is not null)
            {
                _parent.SizeChanged += OnParentSizeChanged;
            }
        }

        private void DetachFromParent()
        {
            if (_parent is not null)
            {
                _parent.SizeChanged -= OnParentSizeChanged;
                _parent = null;
            }
        }

        private void OnParentSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            Schedule();
        }

        private void Schedule()
        {
            if (_pending)
            {
                return;
            }

            _pending = true;
            // Visibility is a layout input: toggling it synchronously from SizeChanged can
            // invalidate the pass that raised the event.
            Dispatcher.UIThread.Post(
                () =>
                {
                    _pending = false;
                    if (_parent is null || !_control.IsAttachedToVisualTree())
                    {
                        return;
                    }

                    var threshold = GetHideBelow(_control);
                    _control.SetCurrentValue(Control.IsVisibleProperty, _parent.Bounds.Width >= threshold);
                },
                DispatcherPriority.Background);
        }
    }

    private sealed class LayoutState
    {
        private readonly Control _control;
        private ColumnDefinitions? _columns;
        private RowDefinitions? _rows;
        private double _columnSpacing, _rowSpacing;
        private List<(Control Control, int Row, int Column, int Rows, int Columns)>? _children;
        private Orientation? _orientation;
        private (int Columns, int Rows)? _uniform;
        private readonly List<TextBlock> _labels = [];
        private bool _updateQueued;

        public LayoutState(Control control)
        {
            _control = control;
            control.SizeChanged += (_, _) => RequestUpdate();
        }

        public void RequestUpdate()
        {
            if (_updateQueued) return;
            _updateQueued = true;
            // Grid raises SizeChanged while an ancestor is measuring cells. Replacing definitions
            // from that callback -- or from a job the layout pass itself drains -- invalidates the
            // cell ranges the Grid is still iterating and throws ArgumentOutOfRangeException out of
            // Grid.MeasureCell. Loaded priority is drained by LayoutManager.ExecuteQueuedLayoutPass,
            // so reflow is posted below the layout and render work instead.
            Dispatcher.UIThread.Post(
                () =>
                {
                    _updateQueued = false;
                    if (TopLevel.GetTopLevel(_control) is null) return;
                    Update(_control.Bounds.Width);
                },
                DispatcherPriority.Background);
        }

        public void Update(double width)
        {
            if (width <= 0) return;
            var narrow = width < GetStackBelow(_control);
            if (_control is StackPanel stack)
            {
                if (narrow && _orientation is null)
                {
                    _orientation = stack.Orientation;
                    stack.SetCurrentValue(StackPanel.OrientationProperty, Orientation.Vertical);
                }
                else if (!narrow && _orientation is { } orientation)
                {
                    stack.SetCurrentValue(StackPanel.OrientationProperty, orientation);
                    _orientation = null;
                }
                return;
            }
            if (_control is UniformGrid uniform)
            {
                if (narrow && _uniform is null)
                {
                    _uniform = (uniform.Columns, uniform.Rows);
                    uniform.SetCurrentValue(UniformGrid.ColumnsProperty, 1);
                    uniform.SetCurrentValue(UniformGrid.RowsProperty, 0);
                }
                else if (!narrow && _uniform is { } original)
                {
                    uniform.SetCurrentValue(UniformGrid.ColumnsProperty, original.Columns);
                    uniform.SetCurrentValue(UniformGrid.RowsProperty, original.Rows);
                    _uniform = null;
                }
                return;
            }
            if (_control is not Grid grid) return;
            if (narrow && _children is null)
            {
                _columns = grid.ColumnDefinitions;
                _rows = grid.RowDefinitions;
                _columnSpacing = grid.ColumnSpacing;
                _rowSpacing = grid.RowSpacing;
                _children = grid.Children
                    .Select(c => (c, Grid.GetRow(c), Grid.GetColumn(c), Grid.GetRowSpan(c), Grid.GetColumnSpan(c)))
                    .OrderBy(c => c.Item2).ThenBy(c => c.Item3).ToList();
                var labels = GetColumnLabels(grid).Split('|');
                var hasLabels = !string.IsNullOrWhiteSpace(GetColumnLabels(grid));
                grid.ColumnDefinitions = new ColumnDefinitions(hasLabels ? "Auto,*" : "*");
                grid.RowDefinitions = new RowDefinitions(string.Join(',', _children.Select(_ => "Auto")));
                grid.SetCurrentValue(Grid.ColumnSpacingProperty, 0d);
                grid.SetCurrentValue(Grid.RowSpacingProperty, Math.Max(8, _rowSpacing));
                for (var i = 0; i < _children.Count; i++)
                {
                    var child = _children[i].Control;
                    Grid.SetRow(child, i);
                    Grid.SetColumn(child, hasLabels ? 1 : 0);
                    if (hasLabels && _children[i].Column < labels.Length)
                    {
                        var label = new TextBlock { Text = labels[_children[i].Column],
                            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MaxWidth = 100,
                            Margin = new Thickness(0, 0, 8, 0) };
                        label.Classes.Add("MptMeta");
                        Grid.SetRow(label, i);
                        grid.Children.Add(label);
                        _labels.Add(label);
                    }
                    Grid.SetRowSpan(child, 1);
                    Grid.SetColumnSpan(child, 1);
                }
            }
            else if (!narrow && _children is not null)
            {
                foreach (var label in _labels) grid.Children.Remove(label);
                _labels.Clear();
                grid.ColumnDefinitions = _columns!;
                grid.RowDefinitions = _rows!;
                grid.SetCurrentValue(Grid.ColumnSpacingProperty, _columnSpacing);
                grid.SetCurrentValue(Grid.RowSpacingProperty, _rowSpacing);
                foreach (var (child, row, column, rows, columns) in _children)
                {
                    Grid.SetRow(child, row);
                    Grid.SetColumn(child, column);
                    Grid.SetRowSpan(child, rows);
                    Grid.SetColumnSpan(child, columns);
                }
                _children = null;
            }
        }
    }
}
