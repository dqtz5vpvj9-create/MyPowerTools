namespace FileTransfer.Surface;

internal sealed partial class AssistantView
{
    private CloudAccountsView? _cloudAccounts;

    internal void OpenCloudAccounts()
    {
        _cloudAccounts?.Deactivate();
        var cloud = new CloudAccountsView(_context);
        _cloudAccounts = cloud;
        cloud.TitleChanged += title => { if (ReferenceEquals(_cloudAccounts, cloud)) _sheetTitle.Text = title; };
        cloud.CloseRequested += CloseSheet;
        cloud.AdvancedRequested += () => { CloseSheet(); _ = OpenAdvancedAsync(); };
        _sheetTitle.Text = "我的网盘";
        _sheetScroll.Content = cloud;
        OpenSheet();
    }

    private void CloseCloudAccounts()
    {
        _cloudAccounts?.Deactivate();
        _cloudAccounts = null;
    }
}
