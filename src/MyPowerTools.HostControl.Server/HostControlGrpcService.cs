using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using MyPowerTools.Broker;
using MyPowerTools.Packaging;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HostProto = MyPowerTools.Protocol.HostControl.V1;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSnapshotDocument = MyPowerTools.Abstractions.SettingsSnapshotDocument;

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

    public override Task<HostProto.ListToolsResponse> ListTools(HostProto.ListToolsRequest request, ServerCallContext context)
    {
        var response = new HostProto.ListToolsResponse();
        response.Tools.AddRange(_runtime.ListTools(request.IncludeDisabled).Select(ToProtoTool));
        return Task.FromResult(response);
    }

    public override Task<HostProto.ToolDescriptor> GetTool(HostProto.GetToolRequest request, ServerCallContext context)
    {
        return Task.FromResult(ToProtoTool(_runtime.GetTool(request.ToolId)));
    }

    public override async Task<HostProto.ListToolsResponse> RefreshTools(HostProto.RefreshToolsRequest request, ServerCallContext context)
    {
        var tools = await _runtime.RefreshToolCatalogAsync(context.CancellationToken);
        var response = new HostProto.ListToolsResponse();
        response.Tools.AddRange(tools.Select(ToProtoTool));
        return response;
    }

    public override Task<HostProto.PublishToolEventResponse> PublishToolEvent(HostProto.PublishToolEventRequest request, ServerCallContext context)
    {
        var payload = JsonNode.Parse(string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson) as JsonObject
                      ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "payload_json must contain a JSON object."));
        var published = _runtime.PublishToolEvent(request.ToolId, request.Topic, payload);
        return Task.FromResult(new HostProto.PublishToolEventResponse { EventSeq = published.Seq });
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

    public override async Task<HostProto.ModuleDetail> SetModuleEnabled(HostProto.SetModuleEnabledRequest request, ServerCallContext context)
    {
        var detail = await _runtime.SetModuleEnabledAsync(request.ModuleId, request.Enabled, context.CancellationToken);
        return ToProtoModuleDetail(detail);
    }

    public override Task<HostProto.ModuleDetail> GetModuleDetail(HostProto.GetModuleDetailRequest request, ServerCallContext context)
    {
        var detail = _runtime.GetModuleDetail(request.ModuleId);
        return Task.FromResult(ToProtoModuleDetail(detail));
    }

    public override Task<HostProto.ListCommandsResponse> ListCommands(HostProto.ListCommandsRequest request, ServerCallContext context)
    {
        var response = new HostProto.ListCommandsResponse();
        response.Commands.AddRange(_runtime.ListCommands(request.Query).Select(ToProtoCommandItem));
        return Task.FromResult(response);
    }

    public override async Task<HostProto.CommandExecutionResponse> ExecuteCommand(HostProto.ExecuteCommandRequest request, ServerCallContext context)
    {
        var result = await _runtime.ExecuteCommandAsync(new CommandRequest(request.InvocationId, request.CommandId, JsonStructMapper.ToJsonObject(request.Args)), context.CancellationToken);
        return ToProtoCommandExecutionResponse(result);
    }

    public override async Task ExecuteCommandStream(HostProto.ExecuteCommandRequest request, IServerStreamWriter<HostProto.CommandExecutionEvent> responseStream, ServerCallContext context)
    {
        await foreach (var evt in _runtime.ExecuteCommandStreamAsync(
            new CommandRequest(request.InvocationId, request.CommandId, JsonStructMapper.ToJsonObject(request.Args)),
            context.CancellationToken))
        {
            var response = new HostProto.CommandExecutionEvent
            {
                InvocationId = evt.InvocationId,
                CommandId = evt.CommandId,
                State = evt.State,
                Message = evt.Message,
                Sequence = (uint)Math.Max(0, evt.Sequence),
                Terminal = evt.Terminal
            };
            if (evt.FinalResult is not null)
            {
                response.FinalResponse = ToProtoCommandExecutionResponse(evt.FinalResult);
            }

            await responseStream.WriteAsync(response);
        }
    }

    public override async Task<HostProto.CancelCommandResponse> CancelCommand(HostProto.CancelCommandRequest request, ServerCallContext context)
    {
        var result = await _runtime.CancelCommandAsync(request.InvocationId, context.CancellationToken);
        return new HostProto.CancelCommandResponse
        {
            Accepted = result.Accepted,
            InvocationId = result.InvocationId,
            State = result.State,
            Message = result.Message
        };
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
        var moduleNotifications = _runtime.ListNotifications()
            .Where(item => string.IsNullOrWhiteSpace(request.ModuleId) || string.Equals(item.ModuleId, request.ModuleId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var filteredNotifications = request.ReadFilter switch
        {
            HostProto.NotificationReadFilter.Read => moduleNotifications.Where(item => item.IsRead),
            HostProto.NotificationReadFilter.Unread => moduleNotifications.Where(item => !item.IsRead),
            _ => moduleNotifications
        };
        var response = new HostProto.ListNotificationsResponse();
        response.TotalCount = (uint)moduleNotifications.Length;
        response.UnreadCount = (uint)moduleNotifications.Count(item => !item.IsRead);
        response.Notifications.AddRange(filteredNotifications
            .Take((int)limit)
            .Select(ToProtoNotification));
        return Task.FromResult(response);
    }

    public override Task<HostProto.SetNotificationReadStateResponse> SetNotificationReadState(
        HostProto.SetNotificationReadStateRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.NotificationId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "notification_id is required."));
        }

        var result = _runtime.SetNotificationReadState(request.NotificationId, request.IsRead);
        if (result is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Notification '{request.NotificationId}' was not found."));
        }

        return Task.FromResult(new HostProto.SetNotificationReadStateResponse
        {
            Notification = ToProtoNotification(result.Notification),
            Changed = result.Changed
        });
    }

    public override Task<HostProto.SettingsSnapshot> GetSettings(HostProto.GetSettingsRequest request, ServerCallContext context)
    {
        var snapshot = _runtime.GetSettings(request.ModuleId);
        return Task.FromResult(ToProtoSettings(snapshot));
    }

    public override async Task<HostProto.SettingsSchema> GetSettingsSchema(HostProto.GetSettingsSchemaRequest request, ServerCallContext context)
    {
        var schema = await _runtime.GetSettingsSchemaAsync(request.ModuleId, context.CancellationToken);
        return new HostProto.SettingsSchema
        {
            ModuleId = schema.ModuleId,
            SchemaJson = schema.SchemaJson
        };
    }

    public override async Task<HostProto.SettingsSnapshot> UpdateSettings(HostProto.UpdateSettingsRequest request, ServerCallContext context)
    {
        try
        {
            var result = await _runtime.UpdateSettingsWithApplyAsync(
                new SettingsPatch(request.ModuleId, request.ExpectedRevision, JsonStructMapper.ToJsonObject(request.Patch)),
                context.CancellationToken);
            return ToProtoSettings(result.Snapshot, result.ApplyState, result.ApplyMessage);
        }
        catch (SettingsConflictException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (SettingsValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex) when (request.ModuleId == ShortcutCatalog.SettingsModuleId &&
                                   ex is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
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

    private static HostProto.NotificationItem ToProtoNotification(NotificationRecord notification)
    {
        return new HostProto.NotificationItem
        {
            Id = notification.Id,
            Time = Timestamp.FromDateTimeOffset(notification.Time),
            ModuleId = notification.ModuleId,
            Level = notification.Level,
            Title = notification.Title,
            Body = notification.Body,
            IsRead = notification.IsRead
        };
    }

    private static HostProto.SettingsSnapshot ToProtoSettings(SettingsSnapshotDocument snapshot, string applyState = "", string applyMessage = "")
    {
        return new HostProto.SettingsSnapshot
        {
            ModuleId = snapshot.ModuleId,
            Revision = snapshot.Revision,
            Values = JsonStructMapper.ToStruct(snapshot.Values),
            UpdatedAt = Timestamp.FromDateTimeOffset(snapshot.UpdatedAt),
            ApplyState = applyState,
            ApplyMessage = applyMessage
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

    private static HostProto.ToolDescriptor ToProtoTool(RuntimeToolSnapshot snapshot)
    {
        var descriptor = snapshot.Descriptor;
        // A tool whose manifest never loaded surfaces as an error card regardless of
        // its module's state or enabled flag.
        var state = !string.IsNullOrWhiteSpace(descriptor.LoadError)
            ? "error"
            : snapshot.Enabled ? snapshot.State : "disabled";
        var stateSummary = !string.IsNullOrWhiteSpace(descriptor.LoadError)
            ? descriptor.LoadError
            : snapshot.Enabled ? snapshot.StateSummary : "This tool is disabled.";
        var response = new HostProto.ToolDescriptor
        {
            ToolId = descriptor.ToolId,
            OwnerModuleId = descriptor.OwnerModuleId,
            Title = descriptor.Title,
            Description = descriptor.Description,
            Icon = descriptor.Icon,
            Category = descriptor.Category,
            PrimaryRouteId = descriptor.PrimaryRouteId,
            Availability = descriptor.Availability,
            ToolType = descriptor.ToolType,
            SourceDirectory = descriptor.SourceDirectory,
            State = state,
            StateSummary = stateSummary,
            HomeCard = new HostProto.ToolHomeCard
            {
                Summary = descriptor.HomeCard.Summary,
                PrimaryActionLabel = descriptor.HomeCard.PrimaryActionLabel,
                StatusBinding = descriptor.HomeCard.StatusBinding,
                Order = descriptor.HomeCard.Order
            }
        };
        response.Routes.AddRange(descriptor.Routes.Select(route => new HostProto.ToolRoute
        {
            RouteId = route.RouteId,
            SurfaceId = route.SurfaceId,
            Title = route.Title,
            Icon = route.Icon,
            SurfaceKind = route.SurfaceKind,
            Source = route.Source,
            StaticRoot = route.StaticRoot,
            Assembly = route.Assembly,
            Type = route.Type,
            OpenExternal = route.OpenExternal
        }));
        foreach (var route in response.Routes.Zip(descriptor.Routes))
        {
            route.First.AllowedOrigins.AddRange(route.Second.AllowedOrigins ?? []);
        }
        if (descriptor.Runtime is { } runtime)
        {
            response.Runtime = new HostProto.ToolRuntime
            {
                Transport = runtime.Transport,
                Endpoint = runtime.Endpoint,
                Command = runtime.Command,
                HealthPath = runtime.HealthPath,
                LogsPath = runtime.LogsPath,
                TimeoutMs = runtime.TimeoutMs,
                Remote = runtime.Remote
            };
            response.Runtime.Args.AddRange(runtime.Args);
        }
        if (descriptor.Settings is { } settings)
        {
            response.Settings = new HostProto.ToolSettings
            {
                SchemaPath = settings.SchemaPath,
                ValuesPath = settings.ValuesPath
            };
            response.Settings.Secrets.AddRange(settings.Secrets);
        }
        response.Commands.AddRange((descriptor.Commands ?? []).Select(command => new HostProto.ToolCommand
        {
            Id = command.Id,
            Title = command.Title,
            Description = command.Description,
            Method = command.Method,
            Path = command.Path
        }));
        return response;
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

    private static HostProto.CommandItem ToProtoCommandItem(MptCommandDescriptor command)
    {
        var item = new HostProto.CommandItem
        {
            CommandId = command.Id,
            ModuleId = command.ModuleId,
            Title = command.Title,
            Subtitle = command.Subtitle,
            Icon = command.Icon,
            DangerLevel = command.DangerLevel,
            RequiresElevation = command.RequiresElevation,
            Kind = command.Kind,
            Category = command.Category,
            Execution = JsonStructMapper.ToStruct(command.Execution ?? new JsonObject()),
            SupportsProgress = command.SupportsProgress,
            SupportsCancellation = command.SupportsCancellation
        };
        item.Constraints.AddRange(command.Constraints ?? []);
        item.Parameters.AddRange((command.Parameters ?? [])
            .Select(parameter => new HostProto.CommandParameter
            {
                Id = parameter.Id,
                Label = parameter.Label,
                Type = parameter.Type,
                Required = parameter.Required,
                DefaultValue = parameter.DefaultValue
            }));
        return item;
    }

    private static HostProto.CommandExecutionResponse ToProtoCommandExecutionResponse(CommandExecutionResult result)
    {
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
        response.Hotkeys.AddRange(diagnostics.Hotkeys.Select(hotkey => new HostProto.RuntimeHotkeyDiagnostics
        {
            Id = hotkey.Id,
            ModuleId = hotkey.ModuleId,
            CommandId = hotkey.CommandId,
            Gesture = hotkey.Gesture,
            Scope = hotkey.Scope,
            State = hotkey.State,
            Message = hotkey.Message,
            IsDefault = hotkey.IsDefault,
            DefaultGesture = hotkey.DefaultGesture
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
                PolicyReason = process.PolicyReason,
                StdoutLineCount = (uint)Math.Max(0, process.StdoutLineCount),
                StderrLineCount = (uint)Math.Max(0, process.StderrLineCount),
                LastStdout = process.LastStdout,
                LastStderr = process.LastStderr
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
        response.Modules.AddRange(diagnostics.Modules.Select(module =>
        {
            var mapped = new HostProto.RuntimeModuleDiagnostics
            {
                ModuleId = module.ModuleId,
                PackageId = module.PackageId,
                DisplayName = module.DisplayName,
                State = module.State,
                Summary = module.Summary,
                Enabled = module.Enabled,
                TransportKind = module.TransportKind,
                UpdatedAt = Timestamp.FromDateTimeOffset(module.UpdatedAt),
                DiagnosticCount = (uint)module.DiagnosticCount,
                ObservationCount = (uint)Math.Max(0, module.ObservationCount),
                ConsecutiveFailureCount = (uint)Math.Max(0, module.ConsecutiveFailureCount),
                SupervisorState = module.SupervisorState,
                SupervisorAction = module.SupervisorAction,
                LastObservedAt = Timestamp.FromDateTimeOffset(module.LastObservedAt),
                TransportSelectionReason = module.TransportSelectionReason,
                ModuleEnabledState = module.ModuleEnabledState,
                TransportActiveState = module.TransportActiveState,
                ToolRuntimeState = module.ToolRuntimeState
            };
            mapped.TransportSelectionDiagnostics.AddRange(module.TransportSelectionDiagnostics);
            return mapped;
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
