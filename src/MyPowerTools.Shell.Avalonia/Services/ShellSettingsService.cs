using Grpc.Core;
using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellSettingsService
{
    public async Task<ShellSettingsSaveResult> SaveAsync(
        SettingsCenterViewModel viewModel,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var patch = JsonStructMapper.ToStruct(ShellPageViewModelFactory.BuildSettingsPatch(viewModel));
            var updated = await client.UpdateSettingsAsync(
                viewModel.SelectedModuleId,
                viewModel.Revision,
                patch,
                cancellationToken);
            return new ShellSettingsSaveResult(
                Saved: true,
                $"{viewModel.SelectedModuleId} settings saved at revision {updated.Revision}");
        }
        catch (RpcException ex)
        {
            return new ShellSettingsSaveResult(Saved: false, ex.Status.Detail);
        }
        catch (Exception ex)
        {
            return new ShellSettingsSaveResult(Saved: false, ex.Message);
        }
    }
}

public sealed record ShellSettingsSaveResult(bool Saved, string StatusText);
