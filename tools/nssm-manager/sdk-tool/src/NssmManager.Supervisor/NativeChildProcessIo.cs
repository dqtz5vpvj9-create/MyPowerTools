using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NssmManager.Contracts;

namespace NssmManager.Supervisor;

internal sealed class NativeChildProcessIo : IAsyncDisposable
{
    private readonly List<Task<uint>> _loggerThreads = [];
    private readonly HashSet<Stream> _ownedStreams = new(ReferenceEqualityComparer.Instance);
    private readonly NssmRotateState _stdoutRotation = new();
    private readonly NssmRotateState _stderrRotation = new();

    private NativeChildProcessIo() { }

    public Stream? StandardInput { get; private set; }
    public Stream? StandardOutput { get; private set; }
    public Stream? StandardError { get; private set; }
    public bool HasStandardHandles => StandardInput is not null || StandardOutput is not null || StandardError is not null;

    public static NativeChildProcessIo Create(NssmServiceConfiguration configuration) =>
        Create(configuration, NativeChildProcess.BuildEnvironmentDictionary(configuration));

    public static NativeChildProcessIo Create(NssmServiceConfiguration configuration, IReadOnlyDictionary<string, string> environment)
    {
        var io = new NativeChildProcessIo();
        try
        {
            if (configuration.AppStdin.Length > 0)
            {
                var input = OpenInput(configuration, environment);
                io.StandardInput = input;
                io._ownedStreams.Add(input);
            }

            if (configuration.AppStdout.Length > 0)
            {
                io.StandardOutput = io.OpenOutput(configuration, environment, configuration.AppStdout, configuration.AppStdoutShareMode,
                    configuration.AppStdoutCreationDisposition, configuration.AppStdoutFlagsAndAttributes,
                    configuration.AppStdoutCopyAndTruncate, io._stdoutRotation);
            }

            if (configuration.AppStderr.Length > 0)
            {
                if (configuration.AppStderr.Equals(configuration.AppStdout, StringComparison.OrdinalIgnoreCase) && io.StandardOutput is not null)
                {
                    io.StandardError = io.StandardOutput;
                }
                else
                {
                    io.StandardError = io.OpenOutput(configuration, environment, configuration.AppStderr, configuration.AppStderrShareMode,
                        configuration.AppStderrCreationDisposition, configuration.AppStderrFlagsAndAttributes,
                        configuration.AppStderrCopyAndTruncate, io._stderrRotation);
                }
            }

            return io;
        }
        catch
        {
            io.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public SafeFileHandle? DuplicateStandardInput() => Duplicate(StandardInput);
    public SafeFileHandle? DuplicateStandardOutput() => Duplicate(StandardOutput);
    public SafeFileHandle? DuplicateStandardError() => Duplicate(StandardError);

    public void RequestRotation()
    {
        if (_stdoutRotation.Value == 1) _stdoutRotation.Value = 2;
        if (_stderrRotation.Value == 1) _stderrRotation.Value = 2;
    }

    private Stream OpenOutput(NssmServiceConfiguration configuration, IReadOnlyDictionary<string, string> environment, string path, uint sharing, uint disposition, uint flags, bool copyAndTruncate, NssmRotateState rotation)
    {
        path = NativeChildProcess.Expand(path, environment);
        if (configuration.RotateFiles)
        {
            NssmIo.rotate_file(configuration.Name, path, configuration.RotateSeconds,
                unchecked((uint)configuration.RotateBytes), unchecked((uint)(configuration.RotateBytes >> 32)),
                configuration.RotateDelayMilliseconds, copyAndTruncate);
        }

        var file = NssmIo.write_to_file(path, sharing, disposition, flags)
            ?? throw new IOException($"CreateFile({path}) failed.");
        var usePipe = configuration.RotateOnline || configuration.TimestampLog || configuration.RedirectHookOutput;
        if (!usePipe)
        {
            _ownedStreams.Add(file);
            return file;
        }

        Stream? read = null;
        Stream? pipe = null;
        uint threadId = 0;
        rotation.Value = configuration.RotateOnline ? 1u : 0u;
        var thread = NssmIo.create_logging_thread(configuration.Name, path, sharing, disposition, flags,
            ref read, ref pipe, file, unchecked((uint)configuration.RotateBytes),
            unchecked((uint)(configuration.RotateBytes >> 32)), configuration.RotateDelayMilliseconds,
            ref threadId, rotation, configuration.TimestampLog, copyAndTruncate);
        if (thread is null || pipe is null)
        {
            read?.Dispose();
            pipe?.Dispose();
            file.Dispose();
            throw new IOException($"Unable to create logging pipe for '{path}'.");
        }
        _loggerThreads.Add(thread);
        _ownedStreams.Add(pipe);
        return pipe;
    }

    private static FileStream OpenInput(NssmServiceConfiguration configuration, IReadOnlyDictionary<string, string> environment)
    {
        var path = NativeChildProcess.Expand(configuration.AppStdin, environment);
        var handle = CreateFile(path, 1, configuration.AppStdinShareMode, IntPtr.Zero,
            configuration.AppStdinCreationDisposition, configuration.AppStdinFlagsAndAttributes, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"CreateFile({path}) failed with error {error}.");
        }
        return new FileStream(handle, FileAccess.Read, 1, isAsync: false);
    }

    private static SafeFileHandle? Duplicate(Stream? stream)
    {
        if (stream is null) return null;
        SafeHandle source = stream switch
        {
            FileStream file => file.SafeFileHandle,
            _ => throw new InvalidOperationException($"Unsupported standard stream type '{stream.GetType()}'.")
        };
        if (NssmIo.dup_handle(source, out var duplicate, "service stream", "child stream") != 0 || duplicate is null)
            throw new IOException("DuplicateHandle() failed for a child standard stream.");
        return duplicate;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in _ownedStreams) stream.Dispose();
        _ownedStreams.Clear();
        await NssmIo.cleanup_loggers(_loggerThreads).ConfigureAwait(false);
        _loggerThreads.Clear();
        StandardInput = StandardOutput = StandardError = null;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
