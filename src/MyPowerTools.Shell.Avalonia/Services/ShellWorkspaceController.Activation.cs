using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    /// <summary>
    /// Serializes activations per tool. A share of several files arrives as one activation per URI,
    /// and they must reach the same live surface in order — not race each other through page loads.
    /// </summary>
    private readonly SemaphoreSlim _activationGate = new(1, 1);

    private readonly Dictionary<string, SurfaceActivationTarget> _activeSurfaceTargets =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True while a tool page actually owns the content host. The page load sets the current tool id
    /// before the surface finishes loading, so this also requires the hosted view to be present. The
    /// mobile shell decides "back" from this rather than from a navigation highlight: a tool page can
    /// be open while the top-level Home item still reads as selected.
    /// </summary>
    public bool IsToolPageOpen =>
        !string.IsNullOrWhiteSpace(_currentToolId) && GetCurrentExternalSdkToolView() is not null;

    /// <summary>True while the top-level Home page is the current page.</summary>
    public bool IsHomePage => string.Equals(_currentPage, HomePage, StringComparison.OrdinalIgnoreCase);

    /// <summary>The page key currently shown in the content host.</summary>
    public string CurrentPageKey => _currentPage;

    /// <summary>
    /// Receives one activation delivered by the platform (share sheet, deep link, "open with", or the
    /// activation pipe). When the requested tool and route are already loaded, the request goes
    /// straight to that surface instance; otherwise the page loads once and the fresh instance
    /// receives it.
    /// </summary>
    internal async Task ActivateToolAsync(ToolActivationRequest activation)
    {
        ArgumentNullException.ThrowIfNull(activation);

        await _activationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await PresentActivationAsync(activation).ConfigureAwait(true);
        }
        finally
        {
            _activationGate.Release();
        }
    }

    /// <summary>
    /// Resolves whether an activation can reuse the surface already on screen and, when it can, the
    /// route that surface should be on. Returns <see langword="null"/> when a page load is required.
    /// An explicit route the surface does not serve cannot be reused: the host has no route-specific
    /// instance to forward to and must reload the page.
    /// </summary>
    public string? ResolveActivationRoute(string? openSurfaceToolId, string? activationToolId, string? requestedRouteId)
    {
        if (string.IsNullOrWhiteSpace(openSurfaceToolId) ||
            string.IsNullOrWhiteSpace(activationToolId) ||
            !string.Equals(openSurfaceToolId, activationToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var requested = (requestedRouteId ?? "").Trim();
        return requested.Length == 0 || string.Equals(requested, _currentToolRouteId, StringComparison.OrdinalIgnoreCase)
            ? _currentToolRouteId
            : null;
    }

    private async Task PresentActivationAsync(ToolActivationRequest activation)
    {
        var hasLiveTarget = TryGetLiveTarget(activation.ToolId, out var open);
        if (hasLiveTarget)
        {
            var live = open;
            _currentToolId = activation.ToolId;
            // An activation without a route targets whatever the tool is showing. Only an explicit
            // route the surface does not serve needs a page load.
            var requested = (activation.RouteId ?? "").Trim();
            if (requested.Length == 0)
            {
                await ForwardActivationAsync(live, activation, _currentToolRouteId).ConfigureAwait(true);
                return;
            }

            if (ResolveActivationRoute(live.ToolId, activation.ToolId, requested) is { } openRoute)
            {
                await ForwardActivationAsync(live, activation, openRoute).ConfigureAwait(true);
                return;
            }
        }

        var descriptor = await TryLoadToolDescriptorAsync(activation.ToolId).ConfigureAwait(true);
        if (descriptor is null)
        {
            return;
        }

        var routeId = ResolveRequestedRouteId(activation, descriptor);
        await ShowToolPageAsync(descriptor, routeId).ConfigureAwait(true);

        var externalView = GetCurrentExternalSdkToolView();
        if (externalView?.ManagedSurface is not IMptAvaloniaSurfaceActivationHandler handler)
        {
            _activeSurfaceTargets.Remove(activation.ToolId);
            SetStatus($"{activation.ToolId} does not handle external activations.");
            return;
        }

        if (hasLiveTarget && ReferenceEquals(open.Handler, handler))
        {
            // Same instance came back from the page load (cached page); it keeps the user's state.
            _currentToolRouteId = routeId;
            await ForwardActivationAsync(open, activation, routeId).ConfigureAwait(true);
            return;
        }

        var target = new SurfaceActivationTarget(activation.ToolId, routeId, handler);
        _activeSurfaceTargets[activation.ToolId] = target;
        _currentToolRouteId = routeId;
        await ForwardActivationAsync(target, activation, routeId).ConfigureAwait(true);
    }

    /// <summary>
    /// Resolves the surface that should receive an activation. The instance currently hosted for the
    /// requested tool wins, whether it was opened by an earlier activation or by hand from the tool
    /// catalog — a manually opened tool is just as live, and rebuilding it would drop the files the
    /// user had already selected. A cached entry only counts while its surface is still the hosted one;
    /// anything else would route an activation into a surface that was already disposed.
    /// </summary>
    private bool TryGetLiveTarget(string toolId, out SurfaceActivationTarget target)
    {
        target = null!;
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return false;
        }

        var hosted = GetCurrentExternalSdkToolView()?.ManagedSurface as IMptAvaloniaSurfaceActivationHandler;
        var hasCached = _activeSurfaceTargets.TryGetValue(toolId, out var cached);
        if (hosted is not null && hasCached && ReferenceEquals(hosted, cached!.Handler))
        {
            target = cached!;
            return true;
        }

        if (hosted is not null &&
            string.Equals(_currentToolId, toolId, StringComparison.OrdinalIgnoreCase))
        {
            var live = new SurfaceActivationTarget(_currentToolId, _currentToolRouteId, hosted);
            _activeSurfaceTargets[toolId] = live;
            target = live;
            return true;
        }

        if (hasCached)
        {
            _activeSurfaceTargets.Remove(toolId);
        }

        return false;
    }

    private async Task ForwardActivationAsync(
        SurfaceActivationTarget target,
        ToolActivationRequest activation,
        string routeId)
    {
        if (!TryGetLiveTarget(target.ToolId, out var current) || !ReferenceEquals(current.Handler, target.Handler))
        {
            // The workspace moved on while this activation was waiting for its turn.
            return;
        }

        _currentToolId = activation.ToolId;
        _currentToolRouteId = routeId;
        var handled = await target.Handler.ActivateAsync(activation).ConfigureAwait(true);
        SetStatus(handled
            ? $"Activated {activation.ToolId}."
            : $"{activation.ToolId} could not resolve the activation target.");
    }

    private static string ResolveRequestedRouteId(
        ToolActivationRequest activation,
        MyPowerTools.Protocol.HostControl.V1.ToolDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(activation.RouteId))
        {
            return activation.RouteId;
        }

        return string.IsNullOrWhiteSpace(descriptor.PrimaryRouteId)
            ? descriptor.Routes.FirstOrDefault()?.RouteId ?? ""
            : descriptor.PrimaryRouteId;
    }

    /// <summary>Identity of a loaded surface that can receive an activation in place.</summary>
    public sealed record SurfaceActivationTarget(
        string ToolId,
        string RouteId,
        IMptAvaloniaSurfaceActivationHandler Handler);
}
