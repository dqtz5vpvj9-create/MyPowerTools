using Android.Graphics;
using Android.Views;
using Avalonia.Controls;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;

namespace MyPowerTools.Android.Rendering;

/// <summary>Connects the compositor's EGL target to the actual Android SurfaceHolder lifetime.</summary>
internal sealed class AndroidEglSurfaceLifecycle : Java.Lang.Object, ISurfaceHolderCallback
{
    private readonly ISurfaceHolder _holder;
    private readonly SurfaceLifetimeGlPlatformSurface[] _surfaces;
    private readonly IPlatformHandle? _window;

    private AndroidEglSurfaceLifecycle(ISurfaceHolder holder,
        SurfaceLifetimeGlPlatformSurface[] surfaces, IPlatformHandle? window)
    {
        _holder = holder;
        _surfaces = surfaces;
        _window = window;
        // Avalonia registered its callback first, so its native handle is current when we run.
        holder.AddCallback(this);
        if (holder.Surface?.IsValid == true) SurfaceCreated(holder);
    }

    public static AndroidEglSurfaceLifecycle? Attach(Control content)
    {
        if (TopLevel.GetTopLevel(content)?.PlatformImpl?.Surfaces is not { } surfaces ||
            surfaces.OfType<SurfaceView>().FirstOrDefault()?.Holder is not { } holder)
        {
            AndroidStartupLog.Info("render-surface", "The Android rendering surface is not ready for its lifecycle callback.");
            return null;
        }

        var wrapped = new List<SurfaceLifetimeGlPlatformSurface>();
        for (var index = 0; index < surfaces.Length; index++)
        {
            if (surfaces[index] is not EglGlPlatformSurface egl) continue;
            var surface = new SurfaceLifetimeGlPlatformSurface(egl);
            surfaces[index] = surface;
            wrapped.Add(surface);
        }

        return new AndroidEglSurfaceLifecycle(holder, wrapped.ToArray(), surfaces.OfType<IPlatformHandle>().FirstOrDefault());
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        foreach (var surface in _surfaces) surface.SurfaceCreated();
        AndroidStartupLog.Info("render-surface",
            $"Created generation={_surfaces.FirstOrDefault()?.Generation} window=0x{_window?.Handle.ToString("x")}");
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        foreach (var surface in _surfaces) surface.SurfaceDestroyed();
        AndroidStartupLog.Info("render-surface", $"Destroyed generation={_surfaces.FirstOrDefault()?.Generation}");
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _holder.RemoveCallback(this);
            foreach (var surface in _surfaces) surface.SurfaceDestroyed();
        }
        base.Dispose(disposing);
    }
}
