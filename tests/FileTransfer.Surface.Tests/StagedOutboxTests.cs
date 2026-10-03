using Avalonia.Headless.XUnit;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// The staged-copy lifecycle the previous TransferView already had and that must survive the phone
/// rewrite: a share that only grants a temporary read is copied into the page's own outbox, that copy
/// is deleted once the module confirms the file was sent, and a user-picked file is never deleted.
/// </summary>
public sealed class StagedOutboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-stage-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly TransferCore _core;

    public StagedOutboxTests()
    {
        Directory.CreateDirectory(_root);
        _core = new TransferCore(_module.Context(_root)) { BusyConfirmDelay = TimeSpan.Zero };
        _core.Attach();
    }

    public void Dispose()
    {
        _core.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task A_confirmed_staged_copy_is_reclaimed_and_a_picked_file_is_not()
    {
        var picked = Path.Combine(_root, "用户选的文件.pdf");
        await File.WriteAllTextAsync(picked, "picked");
        var staged = Stage("分享的文件.png");

        Assert.True(_core.AddFile(picked));
        Assert.True(_core.AddStage(staged));
        Assert.True(_core.IsStaged(staged));
        Assert.False(_core.IsStaged(picked));

        _module.AddPeer("pc-1", "工作电脑");
        await TestPump.RunAsync(() => _core.RefreshAsync());
        await TestPump.RunAsync(() => _core.SendAsync());

        // Both files finish, then the module reports the batch is over. Retiring the confirmed staged
        // copy waits for that report: a per-file event must not free a copy a later file still needs.
        _module.Emit("分享的文件.png", "completed");
        _module.Emit("用户选的文件.pdf", "completed");
        _module.Busy = false;
        await TestPump.SettleAsync(_core);

        Assert.False(File.Exists(staged), "the staged copy should be reclaimed after the module confirmed it");
        Assert.True(File.Exists(picked), "a file the user picked must never be deleted");
        Assert.DoesNotContain(_core.Files, file => file.Staged);
    }

    [AvaloniaFact]
    public async Task A_staged_copy_survives_detach_while_the_module_reports_a_running_transfer()
    {
        var staged = Stage("还没发完.zip");
        _core.AddStage(staged);
        _module.AddPeer("pc-1", "工作电脑");
        await TestPump.RunAsync(() => _core.RefreshAsync());
        await TestPump.RunAsync(() => _core.SendAsync());

        _module.Emit("还没发完.zip", "sending", done: 10, total: 100);
        TestPump.Drain();
        Assert.True(_core.Snapshot.Busy);

        // Detaching mid-transfer must not delete the file the running transfer is still streaming.
        _core.Detach();
        Assert.True(File.Exists(staged), "a staged copy must survive while a transfer can still read it");
    }

    [AvaloniaFact]
    public void Removing_a_pending_staged_file_reclaims_it_immediately()
    {
        var staged = Stage("取消发送.png");
        _core.AddStage(staged);
        Assert.True(File.Exists(staged));

        _core.RemoveFile(staged);
        Assert.False(File.Exists(staged));
        Assert.Empty(_core.Files);
    }

    [AvaloniaFact]
    public async Task Clearing_the_selection_reclaims_staged_copies_and_keeps_user_files()
    {
        var picked = Path.Combine(_root, "保留.pdf");
        await File.WriteAllTextAsync(picked, "keep");
        var staged = Stage("清空.png");
        _core.AddFile(picked);
        _core.AddStage(staged);

        _core.ClearFiles();

        Assert.False(File.Exists(staged));
        Assert.True(File.Exists(picked));
        Assert.Equal(TransferPhase.SelectFiles, _core.Snapshot.Phase);
    }

    [AvaloniaFact]
    public async Task An_old_orphan_staged_copy_is_swept_without_touching_a_pending_one()
    {
        var orphan = Stage("旧文件.bin");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(orphan)!, DateTime.UtcNow.AddDays(-2));
        var pending = Stage("待发送.bin");
        _core.AddStage(pending);

        _module.Busy = false;
        await TestPump.RunAsync(() => _core.RefreshAsync());
        _core.SweepOutbox();

        Assert.False(File.Exists(orphan), "a staged copy older than a day should be swept");
        Assert.True(File.Exists(pending), "a staged copy still in the pending list must stay");
    }

    /// <summary>Creates a real staged copy under the core's own outbox, exactly like the picker does.</summary>
    private string Stage(string name)
    {
        var folder = Path.Combine(_core.OutboxRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "staged");
        return path;
    }
}
