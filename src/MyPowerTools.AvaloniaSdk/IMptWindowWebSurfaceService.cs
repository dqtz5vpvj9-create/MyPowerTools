namespace MyPowerTools.AvaloniaSdk;

/// <summary>
/// Optional capability for web content owned by an independent top-level window.
/// Such a session must not inherit the Shell page's occlusion or keyboard commands.
/// </summary>
public interface IMptWindowWebSurfaceService : IMptWebSurfaceService
{
    IMptWebSurfaceSession CreateWindowSession(MptWebSurfaceRequest request);
}
