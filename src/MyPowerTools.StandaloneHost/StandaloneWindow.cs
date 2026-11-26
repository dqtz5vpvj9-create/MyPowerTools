using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.StandaloneHost;

public sealed class StandaloneWindow : Window
{
    private const string ViewportSurfaceClass = "MptViewportSurface";

    private readonly StandaloneSession _session;
    private readonly ContentControl _surfaceFrame;
    private Control? _activeSurfaceControl;

    internal StandaloneWindow(StandaloneSession session)
    {
        _session = session;
        var descriptor = session.Tool.Descriptor;
        var showAggregationBar = session.Neighborhood.HasSiblingsOrHub || session.Backend.IsHubAttached;

        Width = 1040;
        Height = 720;
        MinWidth = 640;
        MinHeight = 460;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = string.IsNullOrWhiteSpace(descriptor.Title) ? descriptor.ToolId : descriptor.Title;
        this.BindResource(BackgroundProperty, "MptBrushAppBackground");

        _surfaceFrame = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        if (!showAggregationBar)
        {
            // Pure single-tool presence: zero extra chrome, native-feeling window!
            Content = _surfaceFrame;
        }
        else
        {
            var rootGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("32,*")
            };
            var headerBar = BuildAutonomousAggregationBar();
            Grid.SetRow(headerBar, 0);
            Grid.SetRow(_surfaceFrame, 1);
            rootGrid.Children.Add(headerBar);
            rootGrid.Children.Add(_surfaceFrame);
            Content = rootGrid;
        }

        MountRouteSurface(session.Tool.SelectedRoute);
        Closed += OnWindowClosed;
    }

    public Control? ActiveSurfaceControl => _activeSurfaceControl;

    internal Control MountRouteSurface(ToolRoute route)
    {
        DisposeActiveSurface();

        var context = BuildSurfaceContext(route);
        var factory = InstantiateSurfaceFactory(route);
        var control = factory.CreateSurface(context);
        _activeSurfaceControl = control;

        if (control.Classes.Contains(ViewportSurfaceClass))
        {
            _surfaceFrame.Content = control;
        }
        else
        {
            _surfaceFrame.Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = control.Margin == default ? new Thickness(20, 16, 20, 20) : new Thickness(0),
                Content = control
            };
        }

        return control;
    }

    private Border BuildAutonomousAggregationBar()
    {
        var descriptor = _session.Tool.Descriptor;
        var neighborhood = _session.Neighborhood;

        var bar = new Border
        {
            Height = 32,
            Padding = new Thickness(12, 0),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        bar.BindResource(Border.BackgroundProperty, "MptBrushShellChrome");
        bar.BindResource(Border.BorderBrushProperty, "MptBrushDivider");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(descriptor.Title) ? descriptor.ToolId : descriptor.Title,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.BindResource(TextBlock.ForegroundProperty, "MptBrushTextPrimary");
        grid.Children.Add(titleText);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        pill.BindResource(
            Border.BackgroundProperty,
            _session.Backend.IsHubAttached ? "MptBrushNavigationSelected" : "MptBrushSurfaceMuted");
        pill.BindResource(
            Border.BorderBrushProperty,
            _session.Backend.IsHubAttached ? "MptBrushAccent" : "MptBrushBorder");

        var pillLabel = new TextBlock
        {
            Text = _session.Backend.ModeBadgeText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        pillLabel.BindResource(
            TextBlock.ForegroundProperty,
            _session.Backend.IsHubAttached ? "MptBrushAccent" : "MptBrushTextSecondary");
        pill.Child = pillLabel;
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        if (neighborhood.SiblingTools.Count > 0)
        {
            var totalTools = neighborhood.SiblingTools.Count + 1;
            var flyout = new MenuFlyout
            {
                Placement = PlacementMode.BottomEdgeAlignedRight
            };

            var currentItem = new MenuItem
            {
                Header = $"✓ {descriptor.Title}",
                IsEnabled = false
            };
            flyout.Items.Add(currentItem);
            flyout.Items.Add(new Separator());

            foreach (var sibling in neighborhood.SiblingTools)
            {
                var captured = sibling;
                var item = new MenuItem
                {
                    Header = captured.Title
                };
                item.Click += (_, _) => StandaloneNeighborhoodScanner.TryLaunchSiblingTool(captured);
                flyout.Items.Add(item);
            }

            var toolboxButton = new Button
            {
                Content = $"工具箱 ({totalTools}) ▾",
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(8, 2),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Flyout = flyout
            };
            Grid.SetColumn(toolboxButton, 3);
            grid.Children.Add(toolboxButton);
        }

        if (!string.IsNullOrWhiteSpace(neighborhood.HubExecutablePath))
        {
            var hubPath = neighborhood.HubExecutablePath;
            var hubButton = new Button
            {
                Content = "打开聚合主面板",
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(8, 2),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            hubButton.Click += (_, _) => StandaloneNeighborhoodScanner.TryLaunchHub(hubPath, descriptor.ToolId);
            Grid.SetColumn(hubButton, 4);
            grid.Children.Add(hubButton);
        }

        bar.Child = grid;
        return bar;
    }

    private MptAvaloniaSurfaceContext BuildSurfaceContext(ToolRoute route)
    {
        var descriptor = _session.Tool.Descriptor;
        var backend = _session.Backend;

        return new MptAvaloniaSurfaceContext(
            ToolId: descriptor.ToolId,
            RouteId: route.RouteId,
            DataDirectory: backend.SurfaceDataDirectory,
            Theme: "Light",
            ExecuteCommandAsync: backend.ExecuteCommandAsync,
            NavigateAsync: (toolId, routeId, _) => NavigateAsync(string.IsNullOrWhiteSpace(routeId) ? toolId : routeId),
            ServiceUnits: backend.ServiceUnitsFactory.ForTool(descriptor.ToolId),
            Log: entry => System.Diagnostics.Trace.WriteLine($"[{descriptor.ToolId}/{entry.Level}] {entry.Message}"),
            SubscribeEvents: backend.SubscribeSurfaceEvents)
        {
            ExecuteCommandWithInvocationAsync = backend.ExecuteCommandWithInvocationAsync,
            OpenFileAsync = OpenFileWithPlatformAsync
        };
    }

    private Task NavigateAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.CompletedTask;
        }

        var descriptor = _session.Tool.Descriptor;
        var matchingRoute = descriptor.Routes.FirstOrDefault(r =>
            string.Equals(r.RouteId, target, StringComparison.OrdinalIgnoreCase));
        if (matchingRoute is not null)
        {
            MountRouteSurface(matchingRoute);
            return Task.CompletedTask;
        }

        var normalizedId = target.StartsWith("tool:", StringComparison.OrdinalIgnoreCase)
            ? target["tool:".Length..]
            : target;
        var sibling = _session.Neighborhood.SiblingTools.FirstOrDefault(s =>
            string.Equals(s.ToolId, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (sibling is not null)
        {
            StandaloneNeighborhoodScanner.TryLaunchSiblingTool(sibling);
        }

        return Task.CompletedTask;
    }

    private async Task<bool> OpenFileWithPlatformAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (Launcher is { } launcher && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                return await launcher.LaunchUriAsync(uri).ConfigureAwait(false);
            }

            if (Launcher is { } fileLauncher && (File.Exists(path) || Directory.Exists(path)))
            {
                return await fileLauncher.LaunchUriAsync(new Uri(Path.GetFullPath(path))).ConfigureAwait(false);
            }

            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private IMptAvaloniaSurfaceFactory InstantiateSurfaceFactory(ToolRoute route)
    {
        var dllPath = !string.IsNullOrWhiteSpace(route.Assembly) && File.Exists(route.Assembly)
            ? route.Assembly
            : _session.Tool.ResolvedSurfaceAssemblyPath;
        var typeName = !string.IsNullOrWhiteSpace(route.Type)
            ? route.Type
            : _session.Tool.SurfaceFactoryTypeName;

        SharedRuntimeAssemblyResolver.AddProbeDirectory(Path.GetDirectoryName(dllPath));
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
        var factoryType = assembly.GetType(typeName, throwOnError: true)
            ?? throw new TypeLoadException($"Surface factory '{typeName}' was not found in '{dllPath}'.");

        return Activator.CreateInstance(factoryType) as IMptAvaloniaSurfaceFactory
            ?? throw new InvalidOperationException($"Type '{typeName}' does not implement IMptAvaloniaSurfaceFactory.");
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        DisposeActiveSurface();
    }

    private void DisposeActiveSurface()
    {
        var current = Interlocked.Exchange(ref _activeSurfaceControl, null);
        if (current is null)
        {
            return;
        }

        var dataContext = current.DataContext;
        if (!ReferenceEquals(dataContext, current) && dataContext is IDisposable disposableVm)
        {
            try { disposableVm.Dispose(); } catch { }
        }

        if (current is IDisposable disposableControl)
        {
            try { disposableControl.Dispose(); } catch { }
        }
    }
}

internal static class StyledElementResourceExtensions
{
    public static void BindResource(this StyledElement element, AvaloniaProperty property, string resourceKey)
    {
        element.Bind(property, element.GetResourceObservable(resourceKey));
    }
}
