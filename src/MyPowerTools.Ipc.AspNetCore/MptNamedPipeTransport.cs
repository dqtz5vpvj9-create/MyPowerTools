using System.IO.Pipes;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace MyPowerTools.Ipc;

/// <summary>Kestrel integration for the shared named-pipe security policy.</summary>
public static class MptNamedPipeTransport
{
    private static readonly Action<NamedPipeTransportOptions>? TryConfigureRuntimeFactory =
        BuildRuntimeStreamFactorySetter();

    public static void Configure(NamedPipeTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.CurrentUserOnly = false;
        if (OperatingSystem.IsWindows())
        {
            options.PipeSecurity = MptNamedPipePolicy.CreatePipeSecurity();
            TryConfigureRuntimeFactory?.Invoke(options);
        }
    }

    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServerStream(string pipeName, PipeOptions pipeOptions)
    {
        return MptNamedPipePolicy.CreateWindowsServer(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            pipeOptions,
            inBufferSize: 0,
            outBufferSize: 0,
            initializeLowIntegrityLabel: (pipeOptions & PipeOptions.FirstPipeInstance) != 0);
    }

    /// <summary>
    /// On .NET 9+ runtimes (reached via RollForward=Major), Kestrel exposes
    /// NamedPipeTransportOptions.CreateNamedPipeServerStream so the first listener instance
    /// can also apply the Low mandatory integrity label. On .NET 8 runtimes the property does
    /// not exist and Kestrel uses PipeSecurity + CurrentUserOnly=false directly.
    /// </summary>
    private static Action<NamedPipeTransportOptions>? BuildRuntimeStreamFactorySetter()
    {
        var factoryProp = typeof(NamedPipeTransportOptions).GetProperty(
            "CreateNamedPipeServerStream",
            BindingFlags.Public | BindingFlags.Instance);
        if (factoryProp?.SetMethod is null || !factoryProp.PropertyType.IsGenericType)
        {
            return null;
        }

        var typeArgs = factoryProp.PropertyType.GetGenericArguments();
        if (typeArgs.Length != 2 || typeArgs[1] != typeof(NamedPipeServerStream))
        {
            return null;
        }

        var contextType = typeArgs[0];
        var endPointProp = contextType.GetProperty("NamedPipeEndPoint", BindingFlags.Public | BindingFlags.Instance);
        var pipeOptionsProp = contextType.GetProperty("PipeOptions", BindingFlags.Public | BindingFlags.Instance);
        if (endPointProp is null || pipeOptionsProp is null || endPointProp.PropertyType != typeof(NamedPipeEndPoint))
        {
            return null;
        }

        var contextParam = Expression.Parameter(contextType, "context");
        var pipeNameExpr = Expression.Property(
            Expression.Property(contextParam, endPointProp),
            nameof(NamedPipeEndPoint.PipeName));
        var pipeOptionsExpr = Expression.Property(contextParam, pipeOptionsProp);
        var createCall = Expression.Call(
            typeof(MptNamedPipeTransport).GetMethod(nameof(CreateServerStream), BindingFlags.Public | BindingFlags.Static)!,
            pipeNameExpr,
            pipeOptionsExpr);
        var factoryDelegate = Expression.Lambda(factoryProp.PropertyType, createCall, contextParam).Compile();

        return options => factoryProp.SetValue(options, factoryDelegate);
    }
}
