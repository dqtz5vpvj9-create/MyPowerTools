using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>
/// Tool detail. Local tools offer the real surface; computer tools state the device capability they
/// need and never show a result the phone did not receive.
/// </summary>
internal sealed class MobileToolDetailView : UserControl
{
    public MobileToolDetailView(MobileToolDetailViewModel viewModel, IMobileNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(navigator);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Classes.Add("MptMobileSubnav");
        header.Children.Add(MobileElements.BackButton(
            new AsyncRelayCommand(() => navigator.GoBackPageAsync()),
            viewModel.IsComputerTool ? "电脑工具" : "工具"));

        var feature = new StackPanel { Spacing = 8 };
        feature.Classes.Add("MptMobileToolFeature");
        feature.Children.Add(MobileElements.IconBox(viewModel.IconGlyph, "MptMobileIconBoxLarge"));
        var title = MobileElements.PageTitle(viewModel.Title);
        feature.Children.Add(title);
        feature.Children.Add(MobileElements.Body(viewModel.Description));

        var requirement = MobileElements.ListRow(
            viewModel.IsComputerTool ? "\u25A3" : "\u2713",
            viewModel.RequirementTitle,
            viewModel.RequirementDetail,
            viewModel.StatusLabel,
            null,
            viewModel.RequirementTitle);

        var statusDetail = MobileElements.Banner(viewModel.StatusDetail, "MptMobileBannerQuiet");
        statusDetail.Bind(IsVisibleProperty, viewModel, nameof(MobileToolDetailViewModel.HasStatusDetail));

        var actions = new StackPanel { Spacing = 10 };
        if (viewModel.CanOpen)
        {
            actions.Children.Add(MobileElements.Primary("打开工具", viewModel.OpenCommand, $"打开{viewModel.Title}"));
        }
        else if (viewModel.IsComputerTool && viewModel.HasControlDevice)
        {
            var open = MobileElements.Primary("在电脑上打开", viewModel.OpenOnComputerCommand, $"在电脑上打开{viewModel.Title}");
            open.Bind(IsEnabledProperty, viewModel, nameof(MobileToolDetailViewModel.HasControlDevice));
            actions.Children.Add(open);
            var state = MobileElements.Caption($"{viewModel.ControlDeviceName} · {viewModel.ControlDeviceState}");
            actions.Children.Add(state);
        }
        else
        {
            // No imported computer yet: the only honest action is to connect one; the tool never
            // pretends to run on the phone.
            actions.Children.Add(MobileElements.Primary("连接电脑", viewModel.ConnectComputerCommand, "连接电脑"));
            actions.Children.Add(MobileElements.Caption("连接后这里会显示电脑上的真实工具入口。"));
        }

        actions.Children.Add(MobileElements.Secondary("", viewModel.ToggleFavoriteCommand, viewModel.FavoriteActionLabel));
        var favorite = (Button)actions.Children[^1];
        favorite.Bind(ContentControl.ContentProperty, viewModel, nameof(MobileToolDetailViewModel.FavoriteActionLabel));

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(header);
        stack.Children.Add(feature);
        stack.Children.Add(MobileElements.Card(requirement, "MptMobileListCard"));
        stack.Children.Add(statusDetail);
        stack.Children.Add(actions);

        Content = MobileElements.Page(stack);
    }
}
