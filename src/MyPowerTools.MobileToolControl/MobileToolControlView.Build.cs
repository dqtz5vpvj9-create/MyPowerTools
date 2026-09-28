using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyPowerTools.MobileToolControl;

/// <summary>
/// Static structure of the 电脑工具 page: the device list, the device detail page, the three bottom
/// sheets (import, parameters, invocation) and the technical-details layer. The controls are created
/// once and only their content/visibility changes while the page runs.
/// </summary>
internal sealed partial class MobileToolControlView
{
    private Control BuildRoot()
    {
        // The SDK classes own the page background, padding and text defaults; only the internal
        // three-row structure lives here.
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        body.Classes.Add(MobileToolControlTheme.RootClass);

        var header = Stack(6);
        header.Classes.Add(MobileToolControlTheme.PageHeaderClass);
        header.Children.Add(_pageTitle);
        header.Children.Add(_pageSubtitle);
        header.Children.Add(_feedbackText);
        Grid.SetRow(header, 0);
        body.Children.Add(header);

        var content = new Grid();
        content.Children.Add(_deviceListPage);
        content.Children.Add(_devicePage);
        Grid.SetRow(content, 1);
        body.Children.Add(content);

        var footer = Stack(0);
        footer.Classes.Add(MobileToolControlTheme.BottomSpaceClass);
        Grid.SetRow(footer, 2);
        body.Children.Add(footer);

        _pageFrame = new Border { Child = body, Name = "PageFrame" };
        _pageFrame.Classes.Add(MobileToolControlTheme.PageClass);
        MobileToolControlTheme.ApplyBackground(_pageFrame, MobileToolControlTheme.BackgroundBrushKey);
        _pageFrame.SizeChanged += (_, _) => ApplyPageWidth();

        var host = new Grid();
        host.Children.Add(new ScrollViewer
        {
            Content = _pageFrame,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        host.Children.Add(_busyPanel);
        host.Children.Add(_importOverlay);
        host.Children.Add(_parameterOverlay);
        host.Children.Add(_toolOverlay);
        host.Children.Add(_invocationOverlay);
        host.Children.Add(_detailsOverlay);
        return host;
    }

    /// <summary>
    /// The prototype's page padding is 22dp, and 18dp on a 320dp phone; the SDK ships both as
    /// <c>MptMobilePage</c> / <c>MptMobilePageNarrow</c>, so this only toggles the class.
    /// </summary>
    private void ApplyPageWidth()
    {
        var width = _pageFrame?.Bounds.Width ?? 0;
        var narrow = width > 0 && width < MobileToolControlTheme.NarrowPageWidth;
        MobileToolControlTheme.SetClass(_pageFrame!, MobileToolControlTheme.PageNarrowClass, narrow);
    }

    private StackPanel BuildDeviceListPage()
    {
        var page = Stack(0);
        page.Children.Add(_emptyCard);
        page.Children.Add(_deviceRows);
        page.Children.Add(_importButton);
        page.Children.Add(MobileToolControlTheme.Caption(
            "连接码由电脑上的“远程工具访问”设置生成。只能导入 Tailnet 地址，页面不会保存或显示凭据本身。"));
        page.Children.Add(_refreshButton);
        return page;
    }

    private Border BuildEmptyCard()
    {
        var content = Stack(8);
        content.Children.Add(MobileToolControlTheme.SectionTitle("还没有导入电脑"));
        content.Children.Add(MobileToolControlTheme.Caption(
            "在电脑上打开 MPT，进入“远程工具访问”，为这台手机创建授权并复制 mpt://control 连接码。"));
        var card = MobileToolControlTheme.Card();
        card.Child = content;
        return card;
    }

    private StackPanel BuildDevicePage()
    {
        var page = Stack(0);
        page.Children.Add(_backButton);

        var headerCard = MobileToolControlTheme.Card();
        var header = Stack(6);
        header.Children.Add(_deviceName);
        header.Children.Add(MobileToolControlTheme.Caption("电脑上的工具与命令，按电脑返回的授权显示"));
        var pillRow = Stack(8);
        pillRow.Orientation = Orientation.Horizontal;
        pillRow.Children.Add(_deviceStatePill);
        pillRow.Children.Add(_deviceSubtitle);
        header.Children.Add(pillRow);
        header.Children.Add(_deviceEndpoint);
        header.Children.Add(_checkButton);
        var buttonRow = Stack(10);
        buttonRow.Orientation = Orientation.Horizontal;
        buttonRow.Children.Add(_catalogRefreshButton);
        header.Children.Add(buttonRow);
        header.Children.Add(_removeButton);
        headerCard.Child = header;
        page.Children.Add(headerCard);

        page.Children.Add(_catalogErrorNotice);

        page.Children.Add(SectionHeader("常用动作"));
        page.Children.Add(MobileToolControlTheme.Caption(
            "输入监测、屏幕舒适与图片快传的动作优先显示；只读动作不会改变电脑状态。"));
        page.Children.Add(_priorityCard);
        page.Children.Add(_emptyCatalogCard);

        page.Children.Add(SectionHeader("全部命令"));
        page.Children.Add(_catalogCaption);
        page.Children.Add(_otherCard);

        page.Children.Add(SectionHeader("这台电脑上的工具"));
        page.Children.Add(MobileToolControlTheme.Caption(
            "工具与状态来自电脑的实际目录；未授权的命令会显示原因，且不会被执行。"));
        page.Children.Add(_toolsCard);

        return page;
    }

    private Border BuildEmptyCatalogCard()
    {
        var content = Stack(8);
        content.Children.Add(MobileToolControlTheme.SectionTitle("电脑还没有返回工具目录"));
        content.Children.Add(MobileToolControlTheme.Caption(
            "点“刷新工具目录”读取一次；也可能是这台电脑的授权里还没有任何命令。"));
        var card = MobileToolControlTheme.Card();
        card.Child = content;
        card.IsVisible = false;
        return card;
    }

    private Border BuildBusyPanel()
    {
        var panel = Stack(8);
        var bar = new ProgressBar { IsIndeterminate = true };
        bar.Classes.Add(MobileToolControlTheme.ProgressClass);
        panel.Children.Add(bar);
        panel.Children.Add(_busyText);

        var card = MobileToolControlTheme.Card();
        card.Child = panel;
        card.VerticalAlignment = VerticalAlignment.Bottom;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.Margin = new Thickness(22, 0, 22, 24);
        card.IsVisible = false;
        return card;
    }

    private void BuildImportSheet(StackPanel body)
    {
        body.Children.Add(MobileToolControlTheme.Caption(
            "连接码示例：mpt://control/…（电脑端“远程工具访问”里生成）。"));
        body.Children.Add(_importCodeBox);
        body.Children.Add(_importParseButton);
        body.Children.Add(_importScanButton);
        body.Children.Add(_importPreviewPanel);
        body.Children.Add(_importErrorText);
        body.Children.Add(_importWarning);
        body.Children.Add(_confirmImportButton);
        body.Children.Add(MobileToolControlTheme.Caption(
            "导入只保存地址、名称与凭据：凭据进入系统凭据库，不写入本机偏好、日志或导出。"));
    }

    private void BuildParameterSheet(StackPanel body)
    {
        body.Children.Add(_parameterTitle);
        body.Children.Add(_parameterSubtitle);
        body.Children.Add(_parameterSafetyNotice);
        body.Children.Add(_parameterFieldsPanel);
        body.Children.Add(MobileToolControlTheme.Caption(
            "调用会在电脑上执行，是否触发确认或提权由电脑端的既有策略决定。"));
        body.Children.Add(_runButton);
    }

    private void BuildToolSheet(StackPanel body)
    {
        body.Children.Add(_toolSheetTitle);
        body.Children.Add(_toolSheetDetail);
        body.Children.Add(MobileToolControlTheme.Caption(
            "动作与授权状态来自电脑的实际目录；只读动作不会改变电脑状态。"));
        body.Children.Add(_toolSheetRows);
        body.Children.Add(_toolSheetEmpty);
        body.Children.Add(_toolSheetCloseButton);
    }

    private void BuildInvocationSheet(StackPanel body)
    {
        var stateRow = Stack(8);
        stateRow.Orientation = Orientation.Horizontal;
        stateRow.Children.Add(_invocationStatePill);
        stateRow.Children.Add(_invocationCommand);
        body.Children.Add(stateRow);
        body.Children.Add(_invocationMessage);
        body.Children.Add(_invocationProgress);
        body.Children.Add(_invocationProgressCaption);
        body.Children.Add(_invocationCancelNote);
        body.Children.Add(_invocationResultCard);
        body.Children.Add(_detailsButton);
        body.Children.Add(_cancelInvocationButton);
        body.Children.Add(_closeInvocationButton);
    }
}
