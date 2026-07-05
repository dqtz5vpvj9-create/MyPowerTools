using MyPowerTools.Runtime;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HealthCheckSnapshot = MyPowerTools.Abstractions.HealthCheckSnapshot;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;

namespace MyPowerTools.ModuleHost.StdioCompat;

public sealed class StdioCompatModuleHost : IModuleTransportRuntime
{
    public string Kind => "jsonrpc-stdio";

    public ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<ModuleStatusSnapshot?>(new ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            "stopped",
            "stdio compatibility runtime is available on demand.",
            DateTimeOffset.UtcNow,
            [
                new HealthCheckSnapshot("stdio", "stdio compatibility", true, Describe(module.Entrypoint!))
            ],
            module.Status.EventSeq));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(module.Module.Manifest.Id, """{"type":"object","properties":{}}"""));
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
    {
        return await ExecuteAsync(module.Entrypoint!, request, TimeSpan.FromSeconds(30), cancellationToken);
    }

    public async Task<CommandExecutionResult> ExecuteAsync(SelectedEntrypoint entrypoint, CommandRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entrypoint.Command))
        {
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError("MPT_RUNTIME_UNAVAILABLE", "stdio command is missing."));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = entrypoint.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in entrypoint.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError("MPT_RUNTIME_UNAVAILABLE", $"Could not start {entrypoint.Command}."));
        }

        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request.InvocationId,
            ["method"] = "executeCommand",
            ["commandId"] = request.CommandId,
            ["args"] = request.Args.DeepClone()
        }).AsMemory(), timeoutCts.Token);
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        await process.WaitForExitAsync(timeoutCts.Token);

        var output = LogRouter.Redact(await outputTask);
        var error = LogRouter.Redact(await errorTask);
        var success = process.ExitCode == 0;
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            success ? "succeeded" : "failed",
            success,
            success ? output : error,
            success ? null : new MptRuntimeError("MPT_RUNTIME_UNAVAILABLE", error));
    }

    public string Describe(SelectedEntrypoint entrypoint)
    {
        return $"stdio compatibility selected for {entrypoint.Command ?? "script"}; this path is reserved for fallback and development tools.";
    }
}
