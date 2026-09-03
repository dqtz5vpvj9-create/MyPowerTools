using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NssmManager.Contracts;
using NssmManager.Supervisor;

namespace NssmManager.Tests;

public sealed class NssmIoTests
{
    [Fact]
    public void duplicate_and_close_handle_match_win32_contract()
    {
        var path = TemporaryPath();
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            Assert.Equal(0, NssmIo.dup_handle(stream.SafeFileHandle, out SafeFileHandle? duplicate, "source", "destination"));
            Assert.NotNull(duplicate);
            var original = duplicate!.DangerousGetHandle();
            NssmIo.close_handle(ref duplicate, out var remembered);
            Assert.Null(duplicate);
            Assert.Equal(original, remembered);
            NssmIo.close_handle(ref duplicate, out remembered);
            Assert.Equal(new IntPtr(-1), remembered);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task logging_thread_copies_and_timestamps_data()
    {
        var path = TemporaryPath();
        Stream? read = null;
        Stream? pipe = null;
        var rotate = new NssmRotateState { Value = 1 };
        uint threadId = 0;
        var output = NssmIo.write_to_file(path, 3, 4, 128)!;
        var thread = NssmIo.create_logging_thread("svc", path, 3, 4, 128, ref read, ref pipe, output, 0, 0, 0, ref threadId, rotate, timestampLog: true, copyAndTruncate: false);
        Assert.NotNull(thread);
        Assert.NotEqual(0u, threadId);
        await pipe!.WriteAsync("hello\n"u8.ToArray());
        await pipe.FlushAsync();
        pipe.Dispose();
        await thread!.WaitAsync(TimeSpan.FromSeconds(5));
        var text = await File.ReadAllTextAsync(path);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}: hello", text);
        File.Delete(path);
    }

    [Fact]
    public async Task on_demand_rotation_uses_the_production_logger_and_waits_for_newline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nssm-online-rotate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rotate.log");
        try
        {
            await File.WriteAllTextAsync(path, "old\n");
            Stream? read = null;
            Stream? pipe = null;
            var rotate = new NssmRotateState { Value = 2 };
            uint threadId = 0;
            var output = NssmIo.write_to_file(path, 3, 4, 128)!;
            var thread = NssmIo.create_logging_thread("svc", path, 3, 4, 128, ref read, ref pipe, output, 0, 0, 0,
                ref threadId, rotate, timestampLog: false, copyAndTruncate: false);
            Assert.NotNull(thread);
            await pipe!.WriteAsync("first\nsecond"u8.ToArray());
            await pipe.FlushAsync();
            pipe.Dispose();
            await thread!.WaitAsync(TimeSpan.FromSeconds(5));
            var rotated = Directory.GetFiles(directory, "rotate-*.log").Single();
            Assert.Equal("old\nfirst\n", await File.ReadAllTextAsync(rotated));
            Assert.Equal("second", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void character_width_bom_and_timestamp_match_upstream()
    {
        Assert.Equal(1u, NssmIo.guess_charsize("ascii"u8));
        Assert.Equal(2u, NssmIo.guess_charsize(new byte[] { 0xff, 0xfe, (byte)'a', 0, (byte)'b', 0 }));
        var path = TemporaryPath();
        try
        {
            var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            var logger = Logger(path, output, timestamp: true);
            NssmIo.write_bom(logger, out var bom);
            Assert.Equal(2u, bom);
            var complained = 0;
            Assert.Equal(0, NssmIo.write_timestamp(logger, 2, out var timestamp, ref complained));
            Assert.Equal(50u, timestamp);
            output.Flush();
            output.Dispose();
            Assert.Equal(new byte[] { 0xff, 0xfe }, File.ReadAllBytes(path)[..2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void createfile_parameters_use_exact_registry_names_and_defaults()
    {
        WithKey(key =>
        {
            key.SetValue("AppStdout", @"C:\logs\out.log", RegistryValueKind.ExpandString);
            Assert.Equal(0, NssmIo.get_createfile_parameters(key, "AppStdout", 3, 4, 128, true, out var defaults));
            Assert.Equal(3u, defaults.Sharing);
            Assert.Equal(4u, defaults.Disposition);
            Assert.False(defaults.CopyAndTruncate);
            Assert.Equal(0, NssmIo.set_createfile_parameter(key, "AppStdout", "ShareMode", 7));
            Assert.Equal(0, NssmIo.set_createfile_parameter(key, "AppStdout", "CopyAndTruncate", 1));
            Assert.Equal(0, NssmIo.get_createfile_parameters(key, "AppStdout", 3, 4, 128, true, out var custom));
            Assert.Equal(7u, custom.Sharing);
            Assert.True(custom.CopyAndTruncate);
            Assert.Equal(1, NssmIo.delete_createfile_parameter(key, "AppStdout", "ShareMode"));
            Assert.Equal(0, NssmIo.delete_createfile_parameter(key, "AppStdout", "ShareMode"));
        });
    }

    [Fact]
    public void write_to_file_opens_at_end_and_preserves_content()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, "before");
            using (var stream = NssmIo.write_to_file(path, 3, 4, 128)!) stream.Write("-after"u8);
            Assert.Equal("before-after", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void rotated_filename_and_thresholds_match_upstream()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nssm-rotate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "app.log");
        try
        {
            File.WriteAllText(path, "1234");
            var stamp = new DateTime(2024, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);
            Assert.Equal(Path.Combine(directory, "app-20240102T030405.006.log"), NssmIo.rotated_filename(path, stamp));
            NssmIo.rotate_file("svc", path, 0, 0, 5, 0, false);
            Assert.True(File.Exists(path));
            NssmIo.rotate_file("svc", path, 0, 0, 4, 0, false);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(Path.Combine(directory, "app-20240102T030405.006.log")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void output_handles_pass_rotation_thresholds_in_the_declared_argument_order()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "nssm-rotate-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "app.log");
        try
        {
            var configuration = new NssmServiceConfiguration
            {
                Name = "svc",
                AppStdout = path,
                AppStdoutCreationDisposition = 4,
                RotateFiles = true,
                RotateBytes = 64
            };

            File.WriteAllText(path, "1234");
            using (var startup = new NssmIoStartupInfo())
            {
                Assert.Equal(0, NssmIo.get_output_handles(configuration, startup));
            }

            Assert.Empty(Directory.GetFiles(directory, "app-*.log"));

            File.WriteAllText(path, new string('x', 128));
            using (var startup = new NssmIoStartupInfo())
            {
                Assert.Equal(0, NssmIo.get_output_handles(configuration, startup));
            }

            Assert.Single(Directory.GetFiles(directory, "app-*.log"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void output_handle_selection_matches_shared_file_contract()
    {
        var path = TemporaryPath();
        var configuration = new NssmServiceConfiguration
        {
            Name = "svc",
            AppStdout = path,
            AppStderr = path,
            AppStdoutCreationDisposition = 4,
            AppStderrCreationDisposition = 4
        };
        using (var startup = new NssmIoStartupInfo())
        {
            Assert.Equal(0, NssmIo.get_output_handles(configuration, startup));
            Assert.True(startup.UseStandardHandles);
            Assert.NotNull(startup.StandardOutput);
            Assert.NotNull(startup.StandardError);
            using var hook = new NssmIoStartupInfo();
            Assert.Equal(0, NssmIo.use_output_handles(startup, hook));
            Assert.NotNull(hook.StandardOutput);
        }
        File.Delete(path);
    }

    [Fact]
    public async Task cleanup_loggers_waits_for_both_pumps()
    {
        var first = Task.FromResult(2u);
        var second = Task.FromResult(3u);
        await NssmIo.cleanup_loggers([first, second], 1000);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public void retry_read_write_and_timestamp_preserve_bytes()
    {
        var path = TemporaryPath();
        try
        {
            var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            var logger = Logger(path, output, timestamp: true);
            var complained = 0;
            Assert.Equal(0, NssmIo.try_write(logger, "raw"u8, out var raw, ref complained));
            Assert.Equal(3u, raw);
            logger.LineLength = 0;
            Assert.Equal(0, NssmIo.write_with_timestamp(logger, "one\ntwo"u8, out var written, ref complained, 1));
            Assert.True(written > 7);
            output.Flush();
            output.Dispose();
            var text = File.ReadAllText(path);
            Assert.StartsWith("raw", text);
            Assert.Contains(": one\n", text);
            Assert.EndsWith(": two", text);
        }
        finally { File.Delete(path); }
    }

    private static NssmLogger Logger(string path, FileStream output, bool timestamp) => new()
    {
        ServiceName = "svc",
        Path = path,
        ReadHandle = Stream.Null,
        WriteHandle = output,
        Sharing = 3,
        Disposition = 4,
        Flags = 128,
        RotateOnline = new NssmRotateState { Value = 1 },
        TimestampLog = timestamp
    };

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), "nssm-io-" + Guid.NewGuid().ToString("N") + ".tmp");

    private static void WithKey(Action<RegistryKey> action)
    {
        var relative = $@"Software\MyPowerTools\NssmIoTests\{Guid.NewGuid():N}";
        using var key = Registry.CurrentUser.CreateSubKey(relative, writable: true)!;
        try { action(key); }
        finally { Registry.CurrentUser.DeleteSubKeyTree(relative, throwOnMissingSubKey: false); }
    }
}
