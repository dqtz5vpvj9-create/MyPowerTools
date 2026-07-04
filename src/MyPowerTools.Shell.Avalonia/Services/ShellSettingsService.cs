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
            var applyState = string.IsNullOrWhiteSpace(updated.ApplyState) ? "stored" : updated.ApplyState;
            var applyMessage = string.IsNullOrWhiteSpace(updated.ApplyMessage)
                ? $"Revision {updated.Revision}"
                : updated.ApplyMessage;
            return new ShellSettingsSaveResult(
                Saved: true,
                applyState switch
                {
                    "applied" => $"{viewModel.SelectedModuleId} settings saved and applied at revision {updated.Revision}.",
                    "apply-failed" => $"{viewModel.SelectedModuleId} settings saved but apply failed: {applyMessage}",
                    _ => $"{viewModel.SelectedModuleId} settings saved at revision {updated.Revision}: {applyMessage}"
                });
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
