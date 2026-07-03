using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using MyPowerTools.Broker;
using MyPowerTools.Packaging;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.HostControl;

public sealed class HostControlGrpcService : HostProto.HostControl.HostControlBase
{
    private readonly MptHostRuntime _runtime;
    private readonly AuditLog _auditLog;
    private readonly IHostApplicationLifetime? _applicationLifetime;
    private readonly PackageStore? _packageStore;

    public HostControlGrpcService(MptHostRuntime runtime, AuditLog? auditLog = null, IHostApplicationLifetime? applicationLifetime = null, PackageStore? packageStore = null)
    {
        _runtime = runtime;
        _auditLog = auditLog ?? new AuditLog(DefaultAuditPath());
        _applicationLifetime = applicationLifetime;
        _packageStore = packageStore;
    }

    public override Task<HostProto.PingResponse> Ping(HostProto.PingRequest request, ServerCallContext context)
    {
        return Task.FromResult(new HostProto.PingResponse
        {
            RunnerVersion = ProtocolConstants.HostVersion,
            State = "running"
        });
    }

    public override async Task<HostProto.DashboardSnapshot> GetDashboardSnapshot(HostProto.DashboardSnapshotRequest request, ServerCallContext context)
    {
        await _runtime.RefreshHealthAsync(context.CancellationToken);
        var snapshot = _runtime.GetDashboardSnapshot();
        var response = new HostProto.DashboardSnapshot
        {
            EventSeq = snapshot.EventSeq
        };

        response.Cards.AddRange(snapshot.Cards.Select(card =>
        {
            var moduleCard = new HostProto.ModuleCard
            {
                ModuleId = card.ModuleId,
                PackageId = card.PackageId,
                Title = card.Title,
                State = card.State,
                Summary = card.Summary
            };
            moduleCard.Metrics.AddRange(card.Metrics.Select(metric => new HostProto.Metric
            {
                Label = metric.Label,
                Value = metric.Value
            }));
            moduleCard.Actions.AddRange(card.Actions.Select(action => new HostProto.QuickAction
            {
                CommandId = action.CommandId,
                Title = action.Title,
                Style = action.Style
            }));
            return moduleCard;
        }));

        response.Alerts.AddRange(snapshot.Alerts.Select(alert => new HostProto.HostAlert
        {
            Id = alert.Id,
            Level = alert.Level,
            Title = alert.Title,
            Body = alert.Body
        }));

        return response;
    }

    public override Task<HostProto.ListPackagesResponse> ListPackages(HostProto.ListPackagesRequest request, ServerCallContext context)
    {
        var response = new HostProto.ListPackagesResponse();
        response.Packages.AddRange(_runtime.ListPackages(request.IncludeDisabled).Select(package =>
        {
            var summary = new HostProto.PackageSummary
            {
                PackageId = package.PackageId,
                DisplayName = package.DisplayName,
                Version = package.Version,
                Publisher = package.Publisher,
                Directory = package.Directory,
                Hashes = package.Hashes,
                TrustState = package.TrustState,
                TrustPolicy = package.TrustPolicy,
                SignaturePath = package.SignaturePath,
                TrustIssueCount = (uint)Math.Max(0, package.TrustIssueCount),
                ModuleCount = (uint)package.ModuleCount,
                SharedRuntimeCount = (uint)package.SharedRuntimeCount
            };
            summary.ModuleIds.AddRange(package.ModuleIds);
            return summary;
        }));

        return Task.FromResult(response);
    }

    public override async Task<HostProto.PackageOperationResult> InstallPackage(HostProto.InstallPackageRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SourceDirectory))
        {
            return PackageOperationUnavailable("install", "", "Package source directory is required.");
        }

        return await RunPackageOperationAsync(
            "install",
            () => _packageStore!.Install(Path.GetFullPath(request.SourceDirectory)),
            reloadRuntime: true,
            context.CancellationToken);
    }

    public override async Task<HostProto.PackageOperationResult> RepairPackage(HostProto.PackageOperationRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            return PackageOperationUnavailable("repair", "", "Package id is required.");
        }

        return await RunPackageOperationAsync(
            "repair",
            () =>
            {
                var issues = _packageStore!.Repair(request.PackageId);
                return new PackageInstallResult(
                    !issues.Any(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                    request.PackageId,
                    Path.Combine(_packageStore.StoreRoot, request.PackageId),
                    issues);
            },
            reloadRuntime: false,
            context.CancellationToken);
    }

    public override async Task<HostProto.PackageOperationResult> UninstallPackage(HostProto.PackageOperationRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            return PackageOperationUnavailable("uninstall", "", "Package id is required.");
        }

        return await RunPackageOperationAsync(
            "uninstall",
            () => _packageStore!.Uninstall(request.PackageId),
            reloadRuntime: true,
            context.CancellationToken);
    }

    public override async Task<HostProto.PackageOperationResult> RollbackPackage(HostProto.PackageOperationRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            return PackageOperationUnavailable("rollback", "", "Package id is required.");
        }

        return await RunPackageOperationAsync(
            "rollback",
            () => _packageStore!.Rollback(request.PackageId),
            reloadRuntime: true,
            context.CancellationToken);
    }

    public override Task<HostProto.ListModulesResponse> ListModules(HostProto.ListModulesRequest request, ServerCallContext context)
    {
        var response = new HostProto.ListModulesResponse();
        response.Modules.AddRange(_runtime.ListModules(request.IncludeDisabled).Select(module =>
        {
            var summary = new HostProto.ModuleSummary
            {
                ModuleId = module.Module.Manifest.Id,
                PackageId = module.Module.Manifest.PackageId,
                DisplayName = module.Module.Manifest.DisplayName,
                State = module.Status.State,
                Summary = module.Status.Summary,
                Enabled = module.Status.State != "disabled"
            };
            summary.Permissions.AddRange(module.Module.Manifest.Permissions.Select(ToProtoPermission));
            summary.Requirements.AddRange(module.Module.Manifest.Requires.Select(ToProtoRequirement));
            return summary;
        }));
        return Task.FromResult(response);
    }

    public override Task<HostProto.RuntimeDiagnostics> GetRuntimeDiagnostics(HostProto.RuntimeDiagnosticsRequest request, ServerCallContext context)
    {
        return Task.FromResult(ToProtoRuntimeDiagnostics(_runtime.GetRuntimeDiagnostics()));
    }

    public override async Task<HostProto.RuntimeProcessRestartResult> RestartRuntimeProcess(HostProto.RestartRuntimeProcessRequest request, ServerCallContext context)
    {
        var result = await _runtime.RestartRuntimeProcessAsync(request.TransportKind, request.PoolKey, context.CancellationToken);
        return ToProtoRuntimeProcessRestartResult(result);
    }

    public override async Task<HostProto.RuntimeProcessPolicyResult> SetRuntimeProcessRestartPolicy(HostProto.SetRuntimeProcessRestartPolicyRequest request, ServerCallContext context)
    {
        var source = string.IsNullOrWhiteSpace(request.Source) ? "hostcontrol" : request.Source;
        var expiresAt = request.ExpiresAt?.ToDateTimeOffset();
        var result = await _runtime.SetRuntimeProcessRestartPolicyAsync(request.TransportKind, request.PoolKey, request.Paused, request.Reason, context.CancellationToken, source, expiresAt);
        return ToProtoRuntimeProcessPolicyResult(result);
    }

    public override Task<HostProto.ModuleDetail> SetModuleEnabled(HostProto.SetModuleEnabledRequest request, ServerCallContext context)
    {
        var detail = _runtime.SetModuleEnabled(request.ModuleId, request.Enabled);
        return Task.FromResult(ToProtoModuleDetail(detail));
    }

    public override Task<HostProto.ModuleDetail> GetModuleDetail(HostProto.GetModuleDetailRequest request, ServerCallContext context)
    {
        var detail = _runtime.GetModuleDetail(request.ModuleId);
        return Task.FromResult(ToProtoModuleDetail(detail));
    }

    public override Task<HostProto.ListCommandsResponse> ListCommands(HostProto.ListCommandsRequest request, ServerCallContext context)
    {
        var response = new HostProto.ListCommandsResponse();
        response.Commands.AddRange(_runtime.ListCommands(request.Query).Select(command => new HostProto.CommandItem
        {
            CommandId = command.Id,
            ModuleId = command.ModuleId,
            Title = command.Title,
            Subtitle = command.Subtitle,
            Icon = command.Icon,
            DangerLevel = command.DangerLevel,
            RequiresElevation = command.RequiresElevation
        }));
        return Task.FromResult(response);
    }

    public override async Task<HostProto.CommandExecutionResponse> ExecuteCommand(HostProto.ExecuteCommandRequest request, ServerCallContext context)
    {
        var result = await _runtime.ExecuteCommandAsync(new CommandRequest(request.InvocationId, request.CommandId, JsonStructMapper.ToJsonObject(request.Args)), context.CancellationToken);
        var response = new HostProto.CommandExecutionResponse
        {
            InvocationId = result.InvocationId,
            State = result.State,
            Summary = result.Success ? result.Output : result.Error?.Message ?? "Command failed.",
            LogCursor = result.CommandId
        };

        if (result.Error is not null)
        {
            response.ErrorCode = result.Error.Code;
            response.ErrorMessage = result.Error.Message;
            response.Retryable = result.Error.Retryable;
            response.ErrorDetails = JsonStructMapper.ToStruct(result.Error.Details ?? new JsonObject());
        }

        return response;
    }

    public override Task<HostProto.ListBrokerAuditResponse> ListBrokerAudit(HostProto.ListBrokerAuditRequest request, ServerCallContext context)
    {
        var limit = request.Limit == 0 ? 50 : Math.Min(request.Limit, 200);
        var entries = _auditLog.ReadAll()
            .Where(entry => string.IsNullOrWhiteSpace(request.ModuleId) || string.Equals(entry.ModuleId, request.ModuleId, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(request.ActionId) || string.Equals(entry.ActionId, request.ActionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Time)
            .Take((int)limit)
            .Select(entry => new HostProto.BrokerAuditEntry
            {
                AuditId = entry.AuditId,
                Time = Timestamp.FromDateTimeOffset(entry.Time),
                ModuleId = entry.ModuleId,
                ActionId = entry.ActionId,
                PermissionLevel = entry.PermissionLevel,
                Scope = entry.Scope,
                Reason = entry.Reason,
                RequiresBroker = entry.RequiresBroker,
                Result = entry.Result,
                Rollback = entry.Rollback
            });

        var response = new HostProto.ListBrokerAuditResponse();
        response.Entries.AddRange(entries);
        return Task.FromResult(response);
    }

    public override Task<HostProto.ListNotificationsResponse> ListNotifications(HostProto.ListNotificationsRequest request, ServerCallContext context)
    {
        var limit = request.Limit == 0 ? 50 : Math.Min(request.Limit, 200);
        var response = new HostProto.ListNotificationsResponse();
        response.Notifications.AddRange(_runtime.ListNotifications()
            .Where(item => string.IsNullOrWhiteSpace(request.ModuleId) || string.Equals(item.ModuleId, request.ModuleId, StringComparison.OrdinalIgnoreCase))
            .Take((int)limit)
            .Select(item => new HostProto.NotificationItem
            {
                Id = item.Id,
                Time = Timestamp.FromDateTimeOffset(item.Time),
                ModuleId = item.ModuleId,
                Level = item.Level,
                Title = item.Title,
                Body = item.Body,
                IsRead = item.IsRead
            }));
        return Task.FromResult(response);
    }

    public override Task<HostProto.SettingsSnapshot> GetSettings(HostProto.GetSettingsRequest request, ServerCallContext context)
    {
        var snapshot = _runtime.GetSettings(request.ModuleId);
        return Task.FromResult(ToProtoSettings(snapshot));
    }

    public override Task<HostProto.SettingsSnapshot> UpdateSettings(HostProto.UpdateSettingsRequest request, ServerCallContext context)
    {
        try
        {
            var snapshot = _runtime.UpdateSettings(new SettingsPatch(request.ModuleId, request.ExpectedRevision, JsonStructMapper.ToJsonObject(request.Patch)));
            return Task.FromResult(ToProtoSettings(snapshot));
        }
        catch (SettingsConflictException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task TailLogs(HostProto.TailLogsRequest request, IServerStreamWriter<HostProto.LogEntry> responseStream, ServerCallContext context)
    {
        var cursor = 0;
        foreach (var record in _runtime.TailLogs(request.ModuleId))
        {
            await responseStream.WriteAsync(new HostProto.LogEntry
            {
                ModuleId = request.ModuleId,
                Cursor = (++cursor).ToString(),
                Time = Timestamp.FromDateTimeOffset(record.Time),
                Level = record.Level,
                Message = record.Message
            });
        }
    }

    public override async Task SubscribeHostEvents(HostProto.HostEventsRequest request, IServerStreamWriter<HostProto.HostEvent> responseStream, ServerCallContext context)
    {
        var lastSeq = request.LastEventSeq;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            foreach (var evt in _runtime.HostEventsSince(lastSeq))
            {
                await responseStream.WriteAsync(new HostProto.HostEvent
                {
                    Seq = evt.Seq,
                    Type = evt.Type,
                    SourceId = evt.ModuleId,
                    Time = Timestamp.FromDateTimeOffset(evt.Time),
                    Payload = JsonStructMapper.ToStruct(evt.Payload)
                });
                lastSeq = evt.Seq;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);
        }
    }

    public override Task<Empty> OpenShell(HostProto.OpenShellRequest request, ServerCallContext context)
    {
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> QuitRunner(HostProto.QuitRunnerRequest request, ServerCallContext context)
    {
        _applicationLifetime?.StopApplication();
        return Task.FromResult(new Empty());
    }

    private static HostProto.SettingsSnapshot ToProtoSettings(SettingsSnapshotDocument snapshot)
    {
        return new HostProto.SettingsSnapshot
        {
            ModuleId = snapshot.ModuleId,
            Revision = snapshot.Revision,
            Values = JsonStructMapper.ToStruct(snapshot.Values),
            UpdatedAt = Timestamp.FromDateTimeOffset(snapshot.UpdatedAt)
        };
    }

    private async Task<HostProto.PackageOperationResult> RunPackageOperationAsync(
        string operation,
        Func<PackageInstallResult> action,
        bool reloadRuntime,
        CancellationToken cancellationToken)
    {
        if (_packageStore is null)
        {
            return PackageOperationUnavailable(operation, "", "Package store is not configured for this HostControl service.");
        }

        try
        {
            var result = action();
            if (result.Success && reloadRuntime)
            {
                _runtime.Load(_packageStore.StoreRoot);
                await _runtime.RefreshDynamicCommandsAsync(cancellationToken);
            }

            return ToProtoPackageOperation(operation, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PackageOperationUnavailable(operation, "", ex.Message);
        }
    }

    private HostProto.PackageOperationResult ToProtoPackageOperation(string operation, PackageInstallResult result)
    {
        var response = new HostProto.PackageOperationResult
        {
            Success = result.Success,
            Operation = operation,
            PackageId = result.PackageId,
            TargetPath = result.TargetPath,
            Message = result.Success
                ? $"{operation} completed for {result.PackageId}."
                : result.Issues.FirstOrDefault()?.Message ?? $"{operation} failed for {result.PackageId}.",
            PackageCount = (uint)Math.Max(0, _runtime.ListPackages(includeDisabled: true).Count),
            ModuleCount = (uint)Math.Max(0, _runtime.ListModules(includeDisabled: true).Count)
        };
        response.Issues.AddRange(result.Issues.Select(ToProtoPackageIssue));
        return response;
    }

    private static HostProto.PackageOperationResult PackageOperationUnavailable(string operation, string packageId, string message)
    {
        var response = new HostProto.PackageOperationResult
        {
            Success = false,
            Operation = operation,
            PackageId = packageId,
            Message = message
        };
        response.Issues.Add(new HostProto.PackageIssue
        {
            Severity = "error",
            Message = message
        });
        return response;
    }

    private static HostProto.PackageIssue ToProtoPackageIssue(ValidationIssue issue)
    {
        return new HostProto.PackageIssue
        {
            Path = issue.Path,
            Severity = issue.Severity,
            Message = issue.Message
        };
    }

    private static HostProto.ModuleDetail ToProtoModuleDetail(ModuleDetailSnapshot detail)
    {
        var response = new HostProto.ModuleDetail
        {
            ModuleId = detail.ModuleId,
            PackageId = detail.PackageId,
            DisplayName = detail.DisplayName,
            State = detail.State,
            Summary = detail.Summary,
            DetailSurface = JsonStructMapper.ToStruct(new JsonObject
            {
                ["moduleId"] = detail.ModuleId,
                ["title"] = detail.DisplayName,
                ["state"] = detail.State
            })
        };

        response.Diagnostics.AddRange(detail.Diagnostics.Select(check => new HostProto.Diagnostic
        {
            Id = check.Id,
            Label = check.Label,
            State = check.Ok ? "ok" : "error",
            Detail = check.Message
        }));
        response.Permissions.AddRange(detail.Permissions.Select(permission => new HostProto.ModulePermission
        {
            Id = permission.Id,
            Level = permission.Level,
            Capability = permission.Capability,
            Reason = permission.Reason
        }));
        response.Requirements.AddRange(detail.Requirements.Select(requirement => new HostProto.ModuleRequirement
        {
            Capability = requirement.Capability,
            Required = requirement.Required,
            Reason = requirement.Reason
        }));

        return response;
    }

    private static HostProto.ModulePermission ToProtoPermission(MyPowerTools.Packaging.MptPermissionManifest permission)
    {
        return new HostProto.ModulePermission
        {
            Id = permission.Id,
            Level = permission.Level,
            Capability = permission.Capability ?? "",
            Reason = permission.Reason
        };
    }

    private static HostProto.ModuleRequirement ToProtoRequirement(MyPowerTools.Packaging.MptRequirementManifest requirement)
    {
        return new HostProto.ModuleRequirement
        {
            Capability = requirement.Capability,
            Required = requirement.Required,
            Reason = requirement.Reason ?? ""
        };
    }

    private static HostProto.RuntimeDiagnostics ToProtoRuntimeDiagnostics(RuntimeDiagnosticsSnapshot diagnostics)
    {
        var response = new HostProto.RuntimeDiagnostics
        {
            RunnerVersion = diagnostics.RunnerVersion,
            HostControlProtocolVersion = diagnostics.HostControlProtocolVersion,
            ModuleProtocolVersion = diagnostics.ModuleProtocolVersion,
            PlatformRid = diagnostics.PlatformRid,
            DotnetVersion = diagnostics.DotNetVersion,
            OsDescription = diagnostics.OsDescription,
            ProcessArchitecture = diagnostics.ProcessArchitecture,
            StartedAt = Timestamp.FromDateTimeOffset(diagnostics.StartedAt),
            CollectedAt = Timestamp.FromDateTimeOffset(diagnostics.CollectedAt),
            CurrentEventSeq = diagnostics.CurrentEventSeq,
            Paths = new HostProto.RuntimePathDiagnostics
            {
                Root = diagnostics.Paths.Root,
                Settings = diagnostics.Paths.Settings,
                Logs = diagnostics.Paths.Logs,
                State = diagnostics.Paths.State,
                Packages = diagnostics.Paths.Packages,
                PackageRoot = diagnostics.Paths.PackageRoot
            },
            Counts = new HostProto.RuntimeCountDiagnostics
            {
                PackageCount = (uint)diagnostics.Counts.PackageCount,
                ModuleCount = (uint)diagnostics.Counts.ModuleCount,
                EnabledModuleCount = (uint)diagnostics.Counts.EnabledModuleCount,
                DisabledModuleCount = (uint)diagnostics.Counts.DisabledModuleCount,
                RunningModuleCount = (uint)diagnostics.Counts.RunningModuleCount,
                DegradedModuleCount = (uint)diagnostics.Counts.DegradedModuleCount,
                ErrorModuleCount = (uint)diagnostics.Counts.ErrorModuleCount,
                CommandCount = (uint)diagnostics.Counts.CommandCount,
                DynamicCommandCount = (uint)diagnostics.Counts.DynamicCommandCount,
                NotificationCount = (uint)diagnostics.Counts.NotificationCount,
                CommandHistoryCount = (uint)diagnostics.Counts.CommandHistoryCount
            }
        };

        response.Transports.AddRange(diagnostics.Transports.Select(transport => new HostProto.RuntimeTransportDiagnostics
        {
            Kind = transport.Kind,
            RuntimeRegistered = transport.RuntimeRegistered,
            ModuleCount = (uint)transport.ModuleCount
        }));
        response.Processes.AddRange(diagnostics.Processes.Select(process =>
        {
            var mapped = new HostProto.RuntimeProcessDiagnostics
            {
                TransportKind = process.TransportKind,
                PoolKey = process.PoolKey,
                State = process.State,
                ProcessId = (uint)Math.Max(0, process.ProcessId),
                Endpoint = process.Endpoint,
                StartCount = (uint)Math.Max(0, process.StartCount),
                RestartLimit = (uint)Math.Max(0, process.RestartLimit),
                RestartPolicy = process.RestartPolicy,
                PolicyReason = process.PolicyReason
            };
            if (process.LastStartedAt is not null)
            {
                mapped.LastStartedAt = Timestamp.FromDateTimeOffset(process.LastStartedAt.Value);
            }

            if (process.PolicyExpiresAt is not null)
            {
                mapped.PolicyExpiresAt = Timestamp.FromDateTimeOffset(process.PolicyExpiresAt.Value);
            }

            mapped.ModuleIds.AddRange(process.ModuleIds);
            return mapped;
        }));
        response.ProcessPolicyHistory.AddRange(diagnostics.ProcessPolicyHistory.Select(entry =>
        {
            var mapped = new HostProto.RuntimeProcessPolicyHistoryEntry
            {
                Revision = entry.Revision,
                Time = Timestamp.FromDateTimeOffset(entry.Time),
                TransportKind = entry.TransportKind,
                PoolKey = entry.PoolKey,
                RestartPolicy = entry.RestartPolicy,
                Reason = entry.Reason,
                Source = entry.Source
            };
            if (entry.ExpiresAt is not null)
            {
                mapped.ExpiresAt = Timestamp.FromDateTimeOffset(entry.ExpiresAt.Value);
            }

            mapped.ModuleIds.AddRange(entry.ModuleIds);
            return mapped;
        }));
        response.Modules.AddRange(diagnostics.Modules.Select(module => new HostProto.RuntimeModuleDiagnostics
        {
            ModuleId = module.ModuleId,
            PackageId = module.PackageId,
            DisplayName = module.DisplayName,
            State = module.State,
            Enabled = module.Enabled,
            TransportKind = module.TransportKind,
            UpdatedAt = Timestamp.FromDateTimeOffset(module.UpdatedAt),
            DiagnosticCount = (uint)module.DiagnosticCount
        }));
        response.RecentCommands.AddRange(diagnostics.RecentCommands.Select(command => new HostProto.RuntimeCommandHistoryEntry
        {
            InvocationId = command.InvocationId,
            CommandId = command.CommandId,
            ModuleId = command.ModuleId,
            StartedAt = Timestamp.FromDateTimeOffset(command.StartedAt),
            State = command.State,
            Summary = command.Summary
        }));

        return response;
    }

    private static HostProto.RuntimeProcessRestartResult ToProtoRuntimeProcessRestartResult(RuntimeProcessRestartResult result)
    {
        var response = new HostProto.RuntimeProcessRestartResult
        {
            Success = result.Success,
            TransportKind = result.TransportKind,
            PoolKey = result.PoolKey,
            State = result.State,
            Message = result.Message
        };
        response.ModuleIds.AddRange(result.ModuleIds);
        return response;
    }

    private static HostProto.RuntimeProcessPolicyResult ToProtoRuntimeProcessPolicyResult(RuntimeProcessPolicyResult result)
    {
        var response = new HostProto.RuntimeProcessPolicyResult
        {
            Success = result.Success,
            TransportKind = result.TransportKind,
            PoolKey = result.PoolKey,
            State = result.State,
            RestartPolicy = result.RestartPolicy,
            Message = result.Message
        };
        if (result.ExpiresAt is not null)
        {
            response.ExpiresAt = Timestamp.FromDateTimeOffset(result.ExpiresAt.Value);
        }

        response.ModuleIds.AddRange(result.ModuleIds);
        return response;
    }

    private static string DefaultAuditPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "logs", "broker-audit.jsonl");
    }
}
