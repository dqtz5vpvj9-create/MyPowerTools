using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Surfaces;
using Avalonia.Platform;

namespace MyPowerTools.Android.Rendering;

/// <summary>
/// A native window address is not a surface lifetime: Android can reuse that address after HOME.
/// Avalonia 12.0.5's EGL target compares only its window address and size, so explicitly retire its
/// target when SurfaceHolder reports destruction, including a replacement with the same address.
/// </summary>
internal sealed class SurfaceLifetimeGlPlatformSurface(IGlPlatformSurface inner) : IGlPlatformSurface
{
    private int _generation;
    private int _available;

    public int Generation => Volatile.Read(ref _generation);

    public void SurfaceCreated()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _available, 1);
    }

    public void SurfaceDestroyed()
    {
        Volatile.Write(ref _available, 0);
        Interlocked.Increment(ref _generation);
    }

    public IGlPlatformSurfaceRenderTarget CreateGlRenderTarget(IGlContext context)
    {
        var generation = Generation;
        if (Volatile.Read(ref _available) == 0)
            throw new RenderTargetNotReadyException();
        return new Target(this, generation, inner.CreateGlRenderTarget(context));
    }

    private sealed class Target(SurfaceLifetimeGlPlatformSurface owner, int generation,
        IGlPlatformSurfaceRenderTarget innerTarget) : IGlPlatformSurfaceRenderTarget
    {
        private bool IsCurrent => owner.Generation == generation && Volatile.Read(ref owner._available) != 0;

        public PlatformRenderTargetState State => IsCurrent
            ? innerTarget.State
            : PlatformRenderTargetState.Corrupted;

        public IGlPlatformSurfaceRenderingSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo)
        {
            // SurfaceDestroyed can arrive after the compositor checks State but before BeginDraw.
            if (!IsCurrent) throw new RenderTargetCorruptedException();
            return innerTarget.BeginDraw(sceneInfo);
        }

        public void Dispose() => innerTarget.Dispose();
    }
}
