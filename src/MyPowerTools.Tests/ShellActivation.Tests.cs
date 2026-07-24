using MyPowerTools.Abstractions;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class ShellActivationTests
{
    [Fact]
    public void Tool_activation_protocol_preserves_the_target_and_encoded_uri()
    {
        var expected = new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=message%2Fid%2042");

        var payload = ToolActivationProtocol.Serialize(expected);
        var parsed = ToolActivationProtocol.Parse([ToolActivationProtocol.ArgumentName, payload]);

        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void Tool_activation_protocol_preserves_shell_window_suppression()
    {
        var expected = new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=message-42")
        {
            SuppressShellWindow = true
        };

        var payload = ToolActivationProtocol.Serialize(expected);
        var parsed = ToolActivationProtocol.Parse([ToolActivationProtocol.ArgumentName, payload]);

        Assert.Equal(expected, parsed);
        Assert.True(parsed!.SuppressShellWindow);
        Assert.False(ShellActivationRequest.ForTool(parsed).ShowShell);
    }

    [Fact]
    public void Product_activation_uri_round_trips_the_generic_surface_envelope()
    {
        var expected = new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=message%2F42")
        {
            SuppressShellWindow = true
        };

        var productUri = ToolActivationProtocol.CreateProductActivationUri(expected);
        var parsed = ToolActivationProtocol.ParseProductActivationUri(productUri.AbsoluteUri);

        Assert.Equal(expected, parsed);
        Assert.Equal("mypowertools", productUri.Scheme);
        Assert.Equal("activate", productUri.Host);
    }

    [Theory]
    [InlineData("mypowertools://activate?payload=%ZZ")]
    [InlineData("mypowertools://other?payload=%7B%7D")]
    [InlineData("https://activate?payload=%7B%7D")]
    public void Product_activation_uri_rejects_malformed_or_wrong_route(string value)
    {
        Assert.Null(ToolActivationProtocol.ParseProductActivationUri(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"ToolId\":\"bad id\",\"RouteId\":\"inbox\",\"ActivationUri\":\"mypowertools://remote-notification?id=1\"}")]
    [InlineData("{\"ToolId\":\"remote-notifications\",\"RouteId\":\"inbox\",\"ActivationUri\":\"relative\"}")]
    public void Tool_activation_protocol_rejects_malformed_or_unsafe_targets(string payload)
    {
        Assert.Null(ToolActivationProtocol.Deserialize(payload));
    }

    [Fact]
    public async Task Shell_activation_pipe_forwards_to_the_running_instance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"MyPowerTools.ShellActivation.Tests.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<ShellActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = new ShellActivationPipe(
            request =>
            {
                received.TrySetResult(request);
                return Task.CompletedTask;
            },
            pipeName);
        pipe.Start();

        var expected = ShellActivationRequest.ForTool(new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=exact-message-42")
        {
            SuppressShellWindow = true
        });
        var forwarded = await ShellActivationPipe.TryForwardAsync(
            expected,
            TimeSpan.FromSeconds(2),
            pipeName: pipeName);
        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(forwarded);
        Assert.Equal(expected, delivered);
        Assert.False(delivered.ShowShell);
    }

    [Fact]
    public async Task Shell_activation_pipe_acknowledges_after_the_ui_handler_completes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"MyPowerTools.ShellActivation.Ack.Tests.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = new ShellActivationPipe(
            async _ =>
            {
                received.TrySetResult(true);
                await releaseHandler.Task;
            },
            pipeName);
        pipe.Start();

        var forwarding = ShellActivationPipe.TryForwardAsync(
            ShellActivationRequest.FocusShell,
            TimeSpan.FromSeconds(2),
            pipeName: pipeName);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(forwarding.IsCompleted);
        releaseHandler.TrySetResult(true);
        Assert.True(await forwarding.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Resident_lifecycle_requests_distinguish_prewarm_and_shutdown()
    {
        Assert.False(ShellActivationRequest.PrewarmShell.ShowShell);
        Assert.False(ShellActivationRequest.PrewarmShell.ShutdownShell);
        Assert.False(ShellActivationRequest.Shutdown.ShowShell);
        Assert.True(ShellActivationRequest.Shutdown.ShutdownShell);
    }

    [Fact]
    public void Shell_instance_lock_allows_one_owner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mutexName = $@"Local\MyPowerTools.Shell.Tests.{Guid.NewGuid():N}";
        using var first = ShellInstanceLock.Acquire(mutexName);
        var secondAcquired = true;
        Exception? contenderError = null;
        var contender = new Thread(() =>
        {
            try
            {
                using var second = ShellInstanceLock.Acquire(mutexName);
                secondAcquired = second.Acquired;
            }
            catch (Exception exception)
            {
                contenderError = exception;
            }
        });
        contender.Start();

        Assert.True(first.Acquired);
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(contenderError);
        Assert.False(secondAcquired);
    }
}
