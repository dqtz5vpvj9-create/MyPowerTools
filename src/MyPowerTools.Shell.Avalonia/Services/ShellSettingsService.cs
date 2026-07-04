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
            var saved = applyState is not "apply-failed-rolled-back";
            var title = applyState switch
            {
                "applied" => "Settings applied",
                "apply-failed-rolled-back" => "Settings rolled back",
                "apply-failed" => "Settings apply failed",
                _ => "Settings stored"
            };
            var statusText = applyState switch
            {
                "applied" => $"{viewModel.SelectedModuleId} settings saved and applied at revision {updated.Revision}.",
                "apply-failed-rolled-back" => $"{viewModel.SelectedModuleId} settings apply failed and rolled back: {applyMessage}",
                "apply-failed" => $"{viewModel.SelectedModuleId} settings saved but apply failed: {applyMessage}",
                _ => $"{viewModel.SelectedModuleId} settings saved at revision {updated.Revision}: {applyMessage}"
            };
            return new ShellSettingsSaveResult(
                Saved: saved,
                StatusText: statusText,
                ApplyState: applyState,
                ApplyTitle: title,
                ApplyMessage: applyMessage,
                Revision: updated.Revision);
        }
        catch (RpcException ex)
        {
            return ShellSettingsSaveResult.Failed(ex.Status.Detail);
        }
        catch (Exception ex)
        {
            return ShellSettingsSaveResult.Failed(ex.Message);
        }
    }
}

public sealed record ShellSettingsSaveResult(
    bool Saved,
    string StatusText,
    string ApplyState,
    string ApplyTitle,
    string ApplyMessage,
    ulong Revision)
{
    public static ShellSettingsSaveResult Failed(string message)
    {
        return new ShellSettingsSaveResult(false, message, "failed", "Settings save failed", message, 0);
    }
}
