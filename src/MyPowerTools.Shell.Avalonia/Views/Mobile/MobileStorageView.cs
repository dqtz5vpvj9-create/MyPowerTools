using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>
/// WeChat-style「存储空间」page: visual segmented storage bar, external directory management,
/// one-tap cache/temp cleanup, and per-file local cache management with automatic protection for
/// in-flight transfers.
/// </summary>
internal sealed class MobileStorageView : UserControl
{
    private static readonly IBrush ColorTransfer = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush ColorCache = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ColorLogs = new SolidColorBrush(Color.Parse("#8B5CF6"));
    private static readonly IBrush ColorSystem = new SolidColorBrush(Color.Parse("#94A3B8"));

    private readonly MobileStorageViewModel _viewModel;
    private readonly Grid _barGrid;
    private readonly WrapPanel _legendWrap;
    private readonly StackPanel _filesList;
    private readonly TextBlock _filesEmpty;

    public MobileStorageView(MobileStorageViewModel viewModel, IMobileNavigator navigator)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(navigator);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Classes.Add("MptMobileSubnav");
        header.Children.Add(MobileElements.BackButton(new AsyncRelayCommand(() => navigator.GoBackPageAsync()), "返回"));
        var refreshBtn = MobileElements.TextButton("刷新", viewModel.RefreshCommand, "刷新存储统计");
        Grid.SetColumn(refreshBtn, 2);
        header.Children.Add(refreshBtn);

        // ---- 1. Hero Overview Card (WeChat-style total size + segmented color bar + legend) ----
        var heroLabel = MobileElements.Caption("MyPowerTools 已用空间");
        var heroSize = new TextBlock
        {
            FontSize = 32,
            LineHeight = 42,
            FontWeight = FontWeight.Bold,
            LetterSpacing = -0.6
        };
        heroSize.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileTextBrush"));
        heroSize.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.TotalAppSizeText));

        var deviceSpace = MobileElements.Caption("");
        deviceSpace.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.DeviceSpaceSummary));

        _barGrid = new Grid { Height = 12 };
        var barHost = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _barGrid,
            Margin = new Thickness(0, 6, 0, 2)
        };
        barHost.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileTrackBrush"));

        _legendWrap = new WrapPanel { Orientation = Orientation.Horizontal };

        var heroCard = MobileElements.Card(new StackPanel
        {
            Spacing = 6,
            Children = { heroLabel, heroSize, deviceSpace, barHost, _legendWrap }
        });

        var statusBanner = MobileElements.Banner("", "MptMobileBannerQuiet");
        var statusBannerText = (TextBlock)statusBanner.Child!;
        statusBannerText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.StatusMessage));
        statusBanner.Bind(IsVisibleProperty, viewModel, nameof(MobileStorageViewModel.HasStatusMessage));

        // ---- 2. External Directory Card ----
        var extPathTitle = MobileElements.Text("", "MptMobileRowTitle");
        extPathTitle.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.ExternalDirectoryPath));
        var extSummary = MobileElements.Text("", "MptMobileRowSubtitle");
        extSummary.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.ExternalDirectorySummary));
        var extMeta = MobileElements.Caption("");
        extMeta.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.ExternalDirectoryMeta));

        var extHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var extIcon = MobileIcons.Badge("folder", 40, 20);
        Grid.SetColumn(extIcon, 0);
        extHeader.Children.Add(extIcon);
        var extCopy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { extPathTitle, extSummary, extMeta } };
        Grid.SetColumn(extCopy, 1);
        extHeader.Children.Add(extCopy);

        var pickFolderBtn = MobileElements.Primary("选择 / 更换外部保存目录", viewModel.PickExternalDirectoryCommand, "选择或更换外部保存目录");
        var fallbackBtn = MobileElements.Secondary("改用系统下载目录（Download/MPT）", viewModel.UseSystemDownloadsCommand, "改用系统下载目录");
        var extCard = MobileElements.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                extHeader,
                MobileElements.Caption("收到的安装包、文档和图片会直接保存到这个外部目录，系统文件管理器可直接浏览、移动或删除。"),
                pickFolderBtn,
                fallbackBtn
            }
        });

        // ---- 3. One-tap Cache Cleanup Card ----
        var cacheSize = MobileElements.Text("", "MptMobileRowTitle");
        cacheSize.FontSize = 20;
        cacheSize.LineHeight = 28;
        cacheSize.FontWeight = FontWeight.Bold;
        cacheSize.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.TotalCleanableCacheText));
        var cacheDetail = MobileElements.Body("");
        cacheDetail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.CacheDetailText));
        var clearCacheBtn = MobileElements.Primary("一键清理缓存", viewModel.ClearCacheCommand, "一键清理缓存");
        var cacheCard = MobileElements.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                BuildBucketHeader("缓存与临时文件", cacheSize),
                cacheDetail,
                clearCacheBtn
            }
        });

        // ---- 4. Transfer Files Local Cache Card (with per-file list) ----
        var transferSize = MobileElements.Text("", "MptMobileRowTitle");
        transferSize.FontSize = 20;
        transferSize.LineHeight = 28;
        transferSize.FontWeight = FontWeight.Bold;
        transferSize.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.TransferSizeText));
        var transferDetail = MobileElements.Body("");
        transferDetail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.TransferDetailText));
        var clearTransferBtn = MobileElements.Secondary("清理全部可释放本地副本", viewModel.ClearTransferFilesCommand, "清理全部可释放本地副本");

        _filesList = new StackPanel { Spacing = 6 };
        _filesEmpty = MobileElements.Caption("暂无占用私有空间的文件副本。");

        var transferCard = MobileElements.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                BuildBucketHeader("文件助手本地缓存", transferSize),
                transferDetail,
                clearTransferBtn,
                _filesList,
                _filesEmpty
            }
        });

        // ---- 5. System / Base Data Card (read-only) ----
        var systemSize = MobileElements.Text("", "MptMobileRowTitle");
        systemSize.FontWeight = FontWeight.SemiBold;
        systemSize.Bind(TextBlock.TextProperty, viewModel, nameof(MobileStorageViewModel.SystemDataSizeText));
        var systemCard = MobileElements.Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                BuildBucketHeader("应用基础与模块数据", systemSize),
                MobileElements.Caption("包含内置模块包、模块热更新与本机配置（维持正常运行所需，不可清理）。")
            }
        });

        Content = MobileElements.Page(
            header,
            MobileElements.PageTitle("存储空间"),
            statusBanner,
            heroCard,
            MobileElements.Section("外部保存目录", extCard),
            MobileElements.Section("空间清理", cacheCard),
            MobileElements.Section("文件缓存明细", transferCard),
            MobileElements.Section("系统与必要数据", systemCard));

        viewModel.BreakdownChanged += RebuildVisuals;
        DetachedFromVisualTree += (_, _) => viewModel.BreakdownChanged -= RebuildVisuals;
        RebuildVisuals();
    }

    private static Grid BuildBucketHeader(string title, TextBlock valueControl)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var label = MobileElements.Text(title, "MptMobileRowTitle");
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);
        valueControl.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(valueControl, 1);
        grid.Children.Add(valueControl);
        return grid;
    }

    private void RebuildVisuals()
    {
        RebuildBarAndLegend();
        RebuildFilesList();
    }

    private void RebuildBarAndLegend()
    {
        _barGrid.Children.Clear();
        _barGrid.ColumnDefinitions.Clear();
        _legendWrap.Children.Clear();

        var b = _viewModel.Breakdown;
        if (b is null) return;

        var segments = new (string Label, long Bytes, IBrush Brush)[]
        {
            ("文件缓存", b.TransferFilesBytes, ColorTransfer),
            ("临时缓存", b.CacheBytes, ColorCache),
            ("日志与旧缓存", b.LogsAndShadowBytes, ColorLogs),
            ("基础数据", b.SystemDataBytes, ColorSystem)
        };

        var total = Math.Max(1L, segments.Sum(s => Math.Max(0L, s.Bytes)));
        var col = 0;
        foreach (var (label, bytes, brush) in segments)
        {
            if (bytes > 0)
            {
                var weight = Math.Max(0.02, (double)bytes / total);
                _barGrid.ColumnDefinitions.Add(new ColumnDefinition(weight, GridUnitType.Star));
                var block = new Border { Background = brush };
                Grid.SetColumn(block, col++);
                _barGrid.Children.Add(block);
            }

            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var text = MobileElements.Caption($"{label} {MobileByteFormatter.Format(bytes)}");
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 6, 14, 0),
                Children = { dot, text }
            };
            _legendWrap.Children.Add(item);
        }
    }

    private void RebuildFilesList()
    {
        _filesList.Children.Clear();
        var files = _viewModel.ManagedFiles;
        _filesEmpty.IsVisible = files.Count == 0;
        _filesList.IsVisible = files.Count > 0;

        foreach (var file in files)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 4) };
            var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var name = MobileElements.Text(file.Name, "MptMobileRowTitle");
            name.MaxLines = 1;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            copy.Children.Add(name);
            copy.Children.Add(MobileElements.Text(file.MetaText, "MptMobileRowSubtitle"));
            Grid.SetColumn(copy, 0);
            row.Children.Add(copy);

            Control trailing;
            if (file.IsProtected)
            {
                trailing = MobileElements.StatusPill("传输中");
            }
            else
            {
                var delBtn = MobileElements.TextButton("清理", file.DeleteCommand, $"清理 {file.Name}");
                delBtn.MinHeight = 36;
                delBtn.Padding = new Thickness(10, 4);
                trailing = delBtn;
            }

            trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 1);
            row.Children.Add(trailing);

            var separator = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 0) };
            separator.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileDividerBrush"));
            _filesList.Children.Add(new StackPanel { Children = { row, separator } });
        }
    }
}
