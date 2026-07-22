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
        if (focusSearch)
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
        }

        await LoadCommandsAsync(_searchBox.Text ?? "");
        SetStatus("Command Palette opened.");
    }

    public async Task CloseCommandPaletteAsync()
    {
        _commandSearchCancellation?.Cancel();
        _chromeViewModel.IsCommandPaletteOpen = false;
        _chromeViewModel.SelectPage(_currentPage);
        _contentHost.Focus();
        SetStatus("Command Palette closed.");
        if (Interlocked.Exchange(ref _homeLoadDeferred, 0) != 0)
        {
            await ShowPageAsync(HomePage);
        }
    }

    public Task DismissPermissionPromptAsync()
    {
        SetOwnedContent(_permissionPanel, null);
        _chromeViewModel.IsPermissionPromptOpen = false;
        SetStatus("Permission prompt dismissed.");
        return Task.CompletedTask;
    }
}
