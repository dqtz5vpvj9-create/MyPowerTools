using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI.Controls;

namespace MyPowerTools.Shell.Avalonia.Views;

/// <summary>Touch navigation around the same workspace, catalog, commands and permission flow.</summary>
public sealed class MobileShellView : UserControl, IAsyncDisposable
{
    private ShellWorkspaceController _workspace = null!;
    private ShellChromeViewModel _chrome = null!;
    private readonly List<Button> _navigationButtons = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ContentControl _pageHost = null!;
    private bool _opened;

    public MobileShellView()
    {
        // Same wiring the desktop window uses (MainWindow.cs): the chrome callbacks read the
        // _workspace field, which Initialize assigns below. Without them every touch-bar tap, refresh
        // and overlay command on the phone was a silent no-op even though selection state still moved.
        var chrome = new ShellChromeViewModel(
            ShellWorkspaceController.PageLabels,
            page => _workspace?.ShowPageAsync(page) ?? Task.CompletedTask,
            () => _workspace?.RefreshAsync() ?? Task.CompletedTask,
            () => _workspace?.OpenCommandPaletteAsync() ?? Task.CompletedTask,
            () => _workspace?.CloseCommandPaletteAsync() ?? Task.CompletedTask,
            () => _workspace?.DismissPermissionPromptAsync() ?? Task.CompletedTask,
            runtimeModeLabel: "ANDROID",
            runtimeIdentityText: "MPT Android");
        var search = new MptSearchBox { PlaceholderText = "搜索工具与命令", MinHeight = 48, Margin = new Thickness(12, 8) };
        // The workspace and the visual tree must share the exact same hosts: a page written into a
        // control that is not in this tree renders nothing.
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var commandHost = new ContentControl();
        var permissionHost = new ContentControl();
        var auditHost = new ContentControl();
        Initialize(
            new ShellWorkspaceController(chrome, search, pageHost, commandHost, permissionHost, auditHost),
            chrome,
            search,
            pageHost,
            commandHost,
            permissionHost,
            auditHost);
    }

    /// <summary>
    /// Builds the touch shell over the supplied workspace. The caller owns page navigation and can
    /// therefore drive the shell in tests without starting a Runner.
    /// </summary>
    internal MobileShellView(
        ShellWorkspaceController workspace,
        ShellChromeViewModel chrome,
        MptSearchBox search)
        : this(
            workspace,
            chrome,
            search,
            new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch },
            new ContentControl(),
            new ContentControl(),
            new ContentControl())
    {
    }

    private MobileShellView(
        ShellWorkspaceController workspace,
        ShellChromeViewModel chrome,
        MptSearchBox search,
        ContentControl pageHost,
        ContentControl commands,
        ContentControl permissions,
        ContentControl audit)
    {
        Initialize(workspace, chrome, search, pageHost, commands, permissions, audit);
    }

    private void Initialize(
        ShellWorkspaceController workspace,
        ShellChromeViewModel chrome,
        MptSearchBox search,
        ContentControl pageHost,
        ContentControl commands,
        ContentControl permissions,
        ContentControl audit)
    {
        Classes.Add("mobile-shell");
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.Shell.Avalonia/"))
        { Source = new Uri("avares://MyPowerTools.Shell.Avalonia/Styles/Mobile.axaml") });
        this.Bind(BackgroundProperty, new DynamicResourceExtension("MptBrushAppBackground"));
        _workspace = workspace;
        _chrome = chrome;
        _pageHost = pageHost;
        var content = pageHost;
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        layout.Children.Add(search);
        var notices = new ItemsControl { ItemsSource = _chrome.InfoBars, Margin = new Thickness(12, 0) };
        notices.ItemTemplate = new FuncDataTemplate<InfoBarItem>((item, _) =>
        {
            var message = new TextBlock { Text = item.Message, TextWrapping = TextWrapping.Wrap };
            var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            actions.Children.Add(new Button { Content = item.ActionLabel, Command = item.ActionCommand, IsVisible = item.HasAction });
            actions.Children.Add(new Button { Content = "关闭", Command = item.DismissCommand });
            var panel = new StackPanel { Spacing = 8, Children = { message, actions } };
            var card = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Child = panel };
            card.Classes.Add("MptCard");
            card.Bind(IsVisibleProperty, new Binding(nameof(item.IsVisible)) { Source = item });
            return card;
        });
        Grid.SetRow(notices, 1); layout.Children.Add(notices);
        var scroller = new ScrollViewer
        {
            Content = content, Margin = new Thickness(12, 4, 12, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroller, 2); layout.Children.Add(scroller);
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), MinHeight = 64 };
        var names = new[] { "首页", "工具", "活动", "设置", "系统" };
        for (var i = 0; i < _chrome.NavigationItems.Count; i++)
        {
            var item = _chrome.NavigationItems[i];
            var label = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            label.Children.Add(new TextBlock { Text = item.IconGlyph, FontSize = 21, HorizontalAlignment = HorizontalAlignment.Center });
            label.Children.Add(new TextBlock { Text = names[i], FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = label, Command = item.NavigateCommand, MinHeight = 60, MinWidth = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(2), CornerRadius = new CornerRadius(10) };
            button.Classes.Add("MobileNavigation");
            button.Classes.Set("selected", item.IsSelected);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(item.IsSelected)) button.Classes.Set("selected", item.IsSelected);
            };
            AutomationProperties.SetName(button, names[i]);
            _navigationButtons.Add(button);
            Grid.SetColumn(button, i); navigation.Children.Add(button);
        }
        Grid.SetRow(navigation, 3); layout.Children.Add(navigation);

        var commandPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12, 0, 12, 8) };
        commandPanel.Children.Add(new Button { Content = "关闭搜索", Command = _chrome.CloseCommandPaletteCommand,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 8) });
        Grid.SetRow(commands, 1); commandPanel.Children.Add(commands);
        var commandOverlay = new Border { Child = commandPanel };
        commandOverlay.Bind(BackgroundProperty, new DynamicResourceExtension("MptBrushAppBackground"));
        commandOverlay.Bind(IsVisibleProperty, new Binding(nameof(_chrome.IsCommandPaletteOpen)) { Source = _chrome });
        Grid.SetRow(commandOverlay, 1); Grid.SetRowSpan(commandOverlay, 2); layout.Children.Add(commandOverlay);

        var permissionContent = new StackPanel { Spacing = 12, Children =
        {
            new Button { Content = "返回", Command = _chrome.DismissPermissionPromptCommand }, permissions, audit
        } };
        var permissionOverlay = new Border { Padding = new Thickness(16), Child = new ScrollViewer { Content = permissionContent } };
        permissionOverlay.Bind(BackgroundProperty, new DynamicResourceExtension("MptBrushAppBackground"));
        permissionOverlay.Bind(IsVisibleProperty, new Binding(nameof(_chrome.IsPermissionPromptOpen)) { Source = _chrome });
        var root = new Grid { Children = { layout, permissionOverlay } };
        Content = root;
        SizeChanged += (_, e) => Resources["MptLayoutPackageCardWidth"] = Math.Max(0, e.NewSize.Width - 32);
        AttachedToVisualTree += async (_, _) =>
        {
            if (_opened) return;
            _opened = true;
            try
            {
                await _workspace.OpenAsync();
            }
            catch (Exception ex)
            {
                content.Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) };
            }
            finally
            {
                // Published after the initial open, not after construction. OpenAsync navigates to the
                // current page and starts a new workspace generation, so an activation that arrived
                // before it finished was discarded and the user landed back on the gallery.
                _ready.TrySetResult();
            }
        };
    }

    /// <summary>
    /// Completes once the workspace finished its initial open and the shell can accept activations.
    /// Hosts that deliver an activation from outside the UI (the Android share intent) should await
    /// this before calling <see cref="ActivateAsync"/>; the method also awaits it internally so a
    /// request that races startup is ordered after it instead of being dropped.
    /// </summary>
    public Task Ready => _ready.Task;

    public async Task ActivateAsync(MyPowerTools.Abstractions.ToolActivationRequest request)
    {
        await Ready.ConfigureAwait(true);
        await _workspace.ActivateToolAsync(request).ConfigureAwait(true);
    }

    /// <summary>
    /// The touch bar buttons in page order, for hosts that need to address the real instances.
    /// </summary>
    internal IReadOnlyList<Button> NavigationButtons => _navigationButtons;

    /// <summary>The host that receives the current page. Also rendered by this shell's visual tree.</summary>
    internal ContentControl PageHost => _pageHost;

    /// <summary>
    /// Android back: close the top overlay, then leave the open tool, then leave a top-level page, and
    /// only report <see langword="false"/> (let the app go to the background) from Home. This reads the
    /// workspace's real page state; the previous navigation-highlight check reported "at Home" while a
    /// shared file was open, so back dropped the user straight out of the app.
    /// </summary>
    public async Task<bool> HandleBackAsync()
    {
        if (_chrome.IsPermissionPromptOpen) { await _workspace.DismissPermissionPromptAsync(); return true; }
        if (_chrome.IsCommandPaletteOpen) { await _workspace.CloseCommandPaletteAsync(); return true; }
        if (_workspace.IsToolPageOpen) { await _workspace.ShowPageAsync("Tools"); return true; }
        if (!_workspace.IsHomePage) { await _workspace.ShowPageAsync("Home"); return true; }
        return false;
    }

    public ValueTask DisposeAsync() => _workspace.DisposeAsync();
}
