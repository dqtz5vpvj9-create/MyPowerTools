using Avalonia;
using Avalonia.Controls;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>动态 tab: only records the runtime actually produced.</summary>
internal sealed class MobileActivityView : UserControl
{
    public MobileActivityView(MobileActivityViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");

        var rows = MobileElements.Items(viewModel.Items, Row);
        var list = MobileElements.Card(rows, "MptMobileListCard");

        var empty = MobileElements.EmptyContainer();
        empty.Children.Add(MobileElements.EmptyTitle("还没有动态"));
        empty.Children.Add(MobileElements.EmptyDetail("传输记录和打开过的工具会出现在这里。"));
        empty.Bind(IsVisibleProperty, viewModel, nameof(MobileActivityViewModel.IsEmpty));

        var notice = MobileElements.Banner("", "MptMobileBannerQuiet");
        var noticeText = (TextBlock)notice.Child!;
        noticeText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileActivityViewModel.Notice));
        notice.Bind(IsVisibleProperty, viewModel, nameof(MobileActivityViewModel.HasNotice));

        var error = MobileElements.Banner("", "MptMobileBannerError");
        var errorText = (TextBlock)error.Child!;
        errorText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileActivityViewModel.ErrorMessage));
        error.Bind(IsVisibleProperty, viewModel, nameof(MobileActivityViewModel.HasError));

        var refresh = MobileElements.Secondary("刷新动态", viewModel.ReloadCommand, "刷新动态");

        Content = MobileElements.Page(
            MobileElements.PageTitle(viewModel.Title),
            MobileElements.Body(viewModel.Subtitle),
            notice,
            error,
            list,
            empty,
            refresh);
    }

    private static Control Row(MobileActivityItemViewModel item) =>
        MobileElements.ListRow(item.IconGlyph, item.Title, item.Detail, item.Meta, item.OpenCommand);
}
