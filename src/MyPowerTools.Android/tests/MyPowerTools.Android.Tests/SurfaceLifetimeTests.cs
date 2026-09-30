using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Surfaces;
using Avalonia.Platform;
using MyPowerTools.Android.Rendering;

namespace MyPowerTools.Android.Tests;

public sealed class SurfaceLifetimeTests
{
    [Fact]
    public void Resume_replaces_the_old_target_even_when_native_address_and_size_are_unchanged()
    {
        var native = new FakeSurface();
        var surface = new SurfaceLifetimeGlPlatformSurface(native);
        surface.SurfaceCreated();
        var first = surface.CreateGlRenderTarget(null!);
        first.BeginDraw(default);
        Assert.True(first.State.IsReady);

        // The Android buffer has been replaced, but the upstream EGL target still reports Ready:
        // it only compared an unchanged native address and dimensions.
        surface.SurfaceDestroyed();
        native.ReplaceBuffer();
        surface.SurfaceCreated();
        Assert.True(native.Targets[0].State.IsReady);
        Assert.True(first.State.IsCorrupted);
        Assert.Throws<RenderTargetCorruptedException>(() => first.BeginDraw(default));
        first.Dispose();

        var resumed = surface.CreateGlRenderTarget(null!);
        resumed.BeginDraw(default);
        Assert.True(resumed.State.IsReady);
        Assert.Equal(2, native.Targets.Count);
        Assert.True(native.Targets[0].Disposed);
        Assert.Equal(0, native.DrawsToAbandonedBuffers);
        Assert.Equal(1, native.Targets[1].DrawCount);
    }

    [Fact]
    public void Background_does_not_create_or_draw_a_target_until_a_surface_exists()
    {
        var native = new FakeSurface();
        var surface = new SurfaceLifetimeGlPlatformSurface(native);
        Assert.Throws<RenderTargetNotReadyException>(() => surface.CreateGlRenderTarget(null!));
        Assert.Empty(native.Targets);

        surface.SurfaceCreated();
        var target = surface.CreateGlRenderTarget(null!);
        Assert.True(target.State.IsReady);
        surface.SurfaceDestroyed();
        Assert.Throws<RenderTargetCorruptedException>(() => target.BeginDraw(default));
        Assert.Throws<RenderTargetNotReadyException>(() => surface.CreateGlRenderTarget(null!));
        Assert.Single(native.Targets);
        Assert.Equal(0, native.Targets[0].DrawCount);
    }

    [Fact]
    public void Ordinary_frames_keep_the_same_target_and_propagate_context_loss()
    {
        var native = new FakeSurface();
        var surface = new SurfaceLifetimeGlPlatformSurface(native);
        surface.SurfaceCreated();
        var target = surface.CreateGlRenderTarget(null!);
        for (var frame = 0; frame < 120; frame++)
        {
            Assert.True(target.State.IsReady);
            target.BeginDraw(default);
        }
        Assert.Single(native.Targets);
        Assert.Equal(120, native.Targets[0].DrawCount);
        native.Targets[0].ContextLost = true;
        Assert.True(target.State.IsCorrupted);
    }

    private sealed class FakeSurface : IGlPlatformSurface
    {
        private int _buffer;
        public List<FakeTarget> Targets { get; } = [];
        public int DrawsToAbandonedBuffers { get; private set; }
        public void ReplaceBuffer() => _buffer++;

        public IGlPlatformSurfaceRenderTarget CreateGlRenderTarget(IGlContext context)
        {
            var target = new FakeTarget(this, _buffer);
            Targets.Add(target);
            return target;
        }

        public sealed class FakeTarget(FakeSurface owner, int buffer) : IGlPlatformSurfaceRenderTarget
        {
            public bool ContextLost { get; set; }
            public bool Disposed { get; private set; }
            public int DrawCount { get; private set; }
            public PlatformRenderTargetState State => ContextLost
                ? PlatformRenderTargetState.Corrupted
                : PlatformRenderTargetState.Ready;
            public IGlPlatformSurfaceRenderingSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo)
            {
                if (owner._buffer != buffer) owner.DrawsToAbandonedBuffers++;
                DrawCount++;
                return null!;
            }
            public void Dispose() => Disposed = true;
        }
    }
}
