namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    public async Task FocusCommandPaletteAsync()
    {
        await OpenCommandPaletteAsync();
    }

    public async Task OpenCommandPaletteAsync(bool focusSearch = true)
    {
        _chromeViewModel.IsCommandPaletteOpen = true;
        _chromeViewModel.SelectPage(CommandsPage);
        await LoadCommandsAsync(_searchBox.Text ?? "");
        if (focusSearch)
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
        }

        SetStatus("Command Palette opened.");
    }

    public Task CloseCommandPaletteAsync()
    {
        _chromeViewModel.IsCommandPaletteOpen = false;
        _chromeViewModel.SelectPage(_currentPage);
        _contentHost.Focus();
        SetStatus("Command Palette closed.");
        return Task.CompletedTask;
    }

    public Task DismissPermissionPromptAsync()
    {
        _permissionPanel.Content = null;
        _chromeViewModel.IsPermissionPromptOpen = false;
        SetStatus("Permission prompt dismissed.");
        return Task.CompletedTask;
    }
}
