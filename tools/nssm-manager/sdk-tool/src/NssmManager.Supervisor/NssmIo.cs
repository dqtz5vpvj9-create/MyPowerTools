using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NssmManager.Compatibility;
using NssmManager.Contracts;
using NssmManager.Windows;

namespace NssmManager.Supervisor;

public sealed class NssmRotateState { public uint Value { get; set; } }

public sealed class NssmLogger
{
    public required string ServiceName { get; init; }
    public required string Path { get; init; }
    public required Stream ReadHandle { get; init; }
    public required FileStream WriteHandle { get; set; }
    public uint Sharing { get; init; }
    public uint Disposition { get; set; }
    public uint Flags { get; init; }
    public ulong SizeThreshold { get; init; }
    public bool TimestampLog { get; init; }
    public long LineLength { get; set; }
    public required NssmRotateState RotateOnline { get; init; }
    public uint RotateDelay { get; init; }
    public bool CopyAndTruncate { get; init; }
}

public sealed record NssmCreateFileParameters(string Path, uint Sharing, uint Disposition, uint Flags, bool CopyAndTruncate);

public sealed class NssmIoStartupInfo : IDisposable
{
    public FileStream? StandardInput { get; set; }
    public FileStream? StandardOutput { get; set; }
    public FileStream? StandardError { get; set; }
    public bool UseStandardHandles => StandardInput is not null || StandardOutput is not null || StandardError is not null;
    public void Dispose() => NssmIo.close_output_handles(this);
}

/// <summary>Function-for-function managed translation of io.cpp.</summary>
public static class NssmIo
{
    private const uint DuplicateSameAccess = 2;
    private const uint RotateOnline = 1;
    private const uint RotateOnlineAsap = 2;
    private const int ComplainedRead = 1;
    private const int ComplainedWrite = 2;
    private const int ComplainedRotate = 4;

    [NssmUpstreamFunction("src/io.cpp", 9, "static int dup_handle(HANDLE source_handle, HANDLE *dest_handle_ptr, TCHAR *source_description, TCHAR *dest_description, unsigned long flags)", "NssmIoTests.duplicate_and_close_handle_match_win32_contract")]
    public static int dup_handle(SafeHandle sourceHandle, out SafeFileHandle? destinationHandle, string sourceDescription, string destinationDescription, uint flags)
    {
        destinationHandle = null;
        if (sourceHandle is null || sourceHandle.IsInvalid) return 2;
        if (!DuplicateHandle(GetCurrentProcess(), sourceHandle.DangerousGetHandle(), GetCurrentProcess(), out var duplicate, 0, true, flags)) return 2;
        destinationHandle = new SafeFileHandle(duplicate, ownsHandle: true);
        return 0;
    }

    [NssmUpstreamFunction("src/io.cpp", 19, "static int dup_handle(HANDLE source_handle, HANDLE *dest_handle_ptr, TCHAR *source_description, TCHAR *dest_description)", "NssmIoTests.duplicate_and_close_handle_match_win32_contract")]
    public static int dup_handle(SafeHandle sourceHandle, out SafeFileHandle? destinationHandle, string sourceDescription, string destinationDescription) =>
        dup_handle(sourceHandle, out destinationHandle, sourceDescription, destinationDescription, DuplicateSameAccess);

    [NssmUpstreamFunction("src/io.cpp", 28, "static HANDLE create_logging_thread(TCHAR *service_name, TCHAR *path, unsigned long sharing, unsigned long disposition, unsigned long flags, HANDLE *read_handle_ptr, HANDLE *pipe_handle_ptr, HANDLE *write_handle_ptr, unsigned long rotate_bytes_low, unsigned long rotate_bytes_high, unsigned long rotate_delay, unsigned long *tid_ptr, unsigned long *rotate_online, bool timestamp_log, bool copy_and_truncate)", "NssmIoTests.logging_thread_copies_and_timestamps_data")]
    public static Task<uint>? create_logging_thread(string serviceName, string path, uint sharing, uint disposition, uint flags, ref Stream? readHandle, ref Stream? pipeHandle, FileStream writeHandle, uint rotateBytesLow, uint rotateBytesHigh, uint rotateDelay, ref uint threadId, NssmRotateState rotateOnline, bool timestampLog, bool copyAndTruncate)
    {
        threadId = 0;
        if (readHandle is null && pipeHandle is null)
        {
            if (!CreatePipe(out var readPipe, out var writePipe, IntPtr.Zero, 0)) return null;
            _ = SetHandleInformation(writePipe, 1, 1);
            readHandle = new FileStream(readPipe, FileAccess.Read, 1, isAsync: false);
            pipeHandle = new FileStream(writePipe, FileAccess.Write, 1, isAsync: false);
        }
        if (readHandle is null) return null;
        var logger = new NssmLogger
        {
            ServiceName = serviceName,
            Path = path,
            Sharing = sharing,
            Disposition = disposition,
            Flags = flags,
            ReadHandle = readHandle,
            WriteHandle = writeHandle,
            SizeThreshold = ((ulong)rotateBytesHigh << 32) | rotateBytesLow,
            RotateOnline = rotateOnline,
            RotateDelay = rotateDelay,
            TimestampLog = timestampLog,
            CopyAndTruncate = copyAndTruncate
        };
        var task = Task.Run(() => log_and_rotate(logger));
        threadId = unchecked((uint)task.Id);
        return task;
    }

    [NssmUpstreamFunction("src/io.cpp", 78, "static inline unsigned long guess_charsize(void *address, unsigned long bufsize)", "NssmIoTests.character_width_bom_and_timestamp_match_upstream")]
    public static uint guess_charsize(ReadOnlySpan<byte> address)
    {
        if (address.IsEmpty) return 1;
        var buffer = address.ToArray();
        return IsTextUnicode(buffer, buffer.Length, IntPtr.Zero) ? 2u : 1u;
    }

    [NssmUpstreamFunction("src/io.cpp", 83, "static inline void write_bom(logger_t *logger, unsigned long *out)", "NssmIoTests.character_width_bom_and_timestamp_match_upstream")]
    public static void write_bom(NssmLogger logger, out uint written)
    {
        var bom = new byte[] { 0xff, 0xfe };
        logger.WriteHandle.Write(bom);
        written = 2;
    }

    [NssmUpstreamFunction("src/io.cpp", 90, "void close_handle(HANDLE *handle, HANDLE *remember)", "NssmIoTests.duplicate_and_close_handle_match_win32_contract")]
    public static void close_handle(ref SafeFileHandle? handle, out IntPtr remember)
    {
        remember = new IntPtr(-1);
        if (handle is null || handle.IsInvalid) return;
        remember = handle.DangerousGetHandle();
        handle.Dispose();
        handle = null;
    }

    [NssmUpstreamFunction("src/io.cpp", 99, "void close_handle(HANDLE *handle)", "NssmIoTests.duplicate_and_close_handle_match_win32_contract")]
    public static void close_handle(ref SafeFileHandle? handle) => close_handle(ref handle, out _);

    [NssmUpstreamFunction("src/io.cpp", 104, "int get_createfile_parameters(HKEY key, TCHAR *prefix, TCHAR *path, unsigned long *sharing, unsigned long default_sharing, unsigned long *disposition, unsigned long default_disposition, unsigned long *flags, unsigned long default_flags, bool *copy_and_truncate)", "NssmIoTests.createfile_parameters_use_exact_registry_names_and_defaults")]
    public static int get_createfile_parameters(RegistryKey key, string prefix, uint defaultSharing, uint defaultDisposition, uint defaultFlags, bool includeCopyAndTruncate, out NssmCreateFileParameters parameters)
    {
        parameters = new NssmCreateFileParameters(string.Empty, defaultSharing, defaultDisposition, defaultFlags, false);
        if (NssmRegistry.expand_parameter(key, prefix, 32768 * sizeof(char), true, false, out var path) != 0) return 2;
        if (path.Length == 0) return 0;
        var sharing = ReadNumber(key, prefix + "ShareMode", defaultSharing, 4, out var sharingError);
        if (sharingError != 0) return sharingError;
        var disposition = ReadNumber(key, prefix + "CreationDisposition", defaultDisposition, 6, out var dispositionError);
        if (dispositionError != 0) return dispositionError;
        var flags = ReadNumber(key, prefix + "FlagsAndAttributes", defaultFlags, 8, out var flagsError);
        if (flagsError != 0) return flagsError;
        var copy = false;
        if (includeCopyAndTruncate)
        {
            var data = ReadNumber(key, prefix + "CopyAndTruncate", 0, 9, out var copyError);
            if (copyError != 0) return copyError;
            copy = data != 0;
        }
        parameters = new NssmCreateFileParameters(path, sharing, disposition, flags, copy);
        return 0;
    }

    [NssmUpstreamFunction("src/io.cpp", 170, "int set_createfile_parameter(HKEY key, TCHAR *prefix, TCHAR *suffix, unsigned long number)", "NssmIoTests.createfile_parameters_use_exact_registry_names_and_defaults")]
    public static int set_createfile_parameter(RegistryKey key, string prefix, string suffix, uint number) =>
        NssmRegistry.set_number(key, prefix + suffix, number);

    [NssmUpstreamFunction("src/io.cpp", 181, "int delete_createfile_parameter(HKEY key, TCHAR *prefix, TCHAR *suffix)", "NssmIoTests.createfile_parameters_use_exact_registry_names_and_defaults")]
    public static int delete_createfile_parameter(RegistryKey key, string prefix, string suffix)
    {
#pragma warning disable CA1416
        try { key.DeleteValue(prefix + suffix); return 1; }
#pragma warning restore CA1416
        catch (ArgumentException) { return 0; }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { return 0; }
    }

    [NssmUpstreamFunction("src/io.cpp", 193, "HANDLE write_to_file(TCHAR *path, unsigned long sharing, SECURITY_ATTRIBUTES *attributes, unsigned long disposition, unsigned long flags)", "NssmIoTests.write_to_file_opens_at_end_and_preserves_content")]
    public static FileStream? write_to_file(string path, uint sharing, uint disposition, uint flags)
    {
        var handle = CreateFile(path, 2, sharing, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        if (SetFilePointerEx(handle, 0, out _, 2)) _ = SetEndOfFile(handle);
        try { return new FileStream(handle, FileAccess.Write, 1, isAsync: (flags & 0x40000000) != 0); }
        catch { handle.Dispose(); return null; }
    }

    [NssmUpstreamFunction("src/io.cpp", 205, "static void rotated_filename(TCHAR *path, TCHAR *rotated, unsigned long rotated_len, SYSTEMTIME *st)", "NssmIoTests.rotated_filename_and_thresholds_match_upstream")]
    public static string rotated_filename(string path, DateTime? systemTime = null)
    {
        var time = (systemTime ?? DateTime.UtcNow).ToUniversalTime();
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var basename = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{basename}-{time:yyyyMMdd'T'HHmmss'.'fff}{extension}");
    }

    [NssmUpstreamFunction("src/io.cpp", 221, "void rotate_file(TCHAR *service_name, TCHAR *path, unsigned long seconds, unsigned long delay, unsigned long low, unsigned long high, bool copy_and_truncate)", "NssmIoTests.rotated_filename_and_thresholds_match_upstream")]
    public static void rotate_file(string serviceName, string path, uint seconds, uint delay, uint low, uint high, bool copyAndTruncate)
    {
        var now = DateTime.UtcNow;
        long lastWrite;
        uint fileSizeLow = 0;
        uint fileSizeHigh = 0;
        using (var file = CreateFile(path, 0, 7, IntPtr.Zero, 3, 128, IntPtr.Zero))
        {
            if (!file.IsInvalid)
            {
                if (GetFileInformationByHandle(file, out var information))
                {
                    lastWrite = information.LastWriteTime.ToLong();
                    fileSizeLow = information.FileSizeLow;
                    fileSizeHigh = information.FileSizeHigh;
                }
                else
                {
                    seconds = low = high = 0;
                    lastWrite = now.ToFileTimeUtc();
                }
            }
            else
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2) return;
                NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_ROTATE_FILE_FAILED"), serviceName, path, "CreateFile()", path, NssmEvent.error_string(unchecked((uint)error)));
                seconds = low = high = 0;
                lastWrite = now.ToFileTimeUtc();
            }
        }

        if (seconds != 0 && lastWrite > now.AddSeconds(-seconds).ToFileTimeUtc()) return;
        if ((low != 0 || high != 0) && (fileSizeHigh < high || (fileSizeHigh == high && fileSizeLow < low))) return;

        var rotated = rotated_filename(path, DateTime.FromFileTimeUtc(lastWrite));
        var function = copyAndTruncate ? "CopyFile()" : "MoveFile()";
        var ok = RotateOnlineFile(path, rotated, delay, copyAndTruncate);
        if (ok)
        {
            NssmEvent.log_event(4, NssmEvent.message_id("NSSM_EVENT_ROTATED"), serviceName, path, rotated);
            return;
        }
        var rotateError = Marshal.GetLastPInvokeError();
        if (rotateError == 2) return;
        NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_ROTATE_FILE_FAILED"), serviceName, path, function, rotated, NssmEvent.error_string(unchecked((uint)rotateError)));
    }

    [NssmUpstreamFunction("src/io.cpp", 306, "int get_output_handles(nssm_service_t *service, STARTUPINFO *si)", "NssmIoTests.output_handle_selection_matches_shared_file_contract")]
    public static int get_output_handles(NssmServiceConfiguration? service, NssmIoStartupInfo? startupInfo)
    {
        if (startupInfo is null || service is null) return 1;
        try
        {
            if (service.AppStdin.Length > 0)
                startupInfo.StandardInput = new FileStream(Environment.ExpandEnvironmentVariables(service.AppStdin), new FileStreamOptions { Mode = NssmFileOptions.Mode(service.AppStdinCreationDisposition), Access = FileAccess.Read, Share = NssmFileOptions.Share(service.AppStdinShareMode), Options = NssmFileOptions.Options(service.AppStdinFlagsAndAttributes) });
            if (service.AppStdout.Length > 0)
            {
                if (service.RotateFiles) rotate_file(service.Name, Environment.ExpandEnvironmentVariables(service.AppStdout), service.RotateSeconds, service.RotateDelayMilliseconds, unchecked((uint)service.RotateBytes), unchecked((uint)(service.RotateBytes >> 32)), service.AppStdoutCopyAndTruncate);
                startupInfo.StandardOutput = write_to_file(Environment.ExpandEnvironmentVariables(service.AppStdout), service.AppStdoutShareMode, service.AppStdoutCreationDisposition, service.AppStdoutFlagsAndAttributes);
                if (startupInfo.StandardOutput is null) return 4;
            }
            if (service.AppStderr.Length > 0)
            {
                var same = service.AppStderr.Equals(service.AppStdout, StringComparison.OrdinalIgnoreCase);
                if (same && startupInfo.StandardOutput is not null)
                    startupInfo.StandardError = DuplicateStream(startupInfo.StandardOutput);
                else
                {
                    if (service.RotateFiles) rotate_file(service.Name, Environment.ExpandEnvironmentVariables(service.AppStderr), service.RotateSeconds, service.RotateDelayMilliseconds, unchecked((uint)service.RotateBytes), unchecked((uint)(service.RotateBytes >> 32)), service.AppStderrCopyAndTruncate);
                    startupInfo.StandardError = write_to_file(Environment.ExpandEnvironmentVariables(service.AppStderr), service.AppStderrShareMode, service.AppStderrCreationDisposition, service.AppStderrFlagsAndAttributes);
                    if (startupInfo.StandardError is null) return 7;
                }
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return 2; }
    }

    [NssmUpstreamFunction("src/io.cpp", 404, "int use_output_handles(nssm_service_t *service, STARTUPINFO *si)", "NssmIoTests.output_handle_selection_matches_shared_file_contract")]
    public static int use_output_handles(NssmIoStartupInfo? source, NssmIoStartupInfo? destination)
    {
        if (source is null || destination is null) return 1;
        try
        {
            if (source.StandardOutput is not null) destination.StandardOutput = DuplicateStream(source.StandardOutput);
            if (source.StandardError is not null) destination.StandardError = DuplicateStream(source.StandardError);
            return 0;
        }
        catch { destination.Dispose(); return 2; }
    }

    [NssmUpstreamFunction("src/io.cpp", 426, "void close_output_handles(STARTUPINFO *si)", "NssmIoTests.output_handle_selection_matches_shared_file_contract")]
    public static void close_output_handles(NssmIoStartupInfo? startupInfo)
    {
        if (startupInfo is null) return;
        var streams = new HashSet<FileStream>(ReferenceEqualityComparer.Instance);
        if (startupInfo.StandardInput is not null) streams.Add(startupInfo.StandardInput);
        if (startupInfo.StandardOutput is not null) streams.Add(startupInfo.StandardOutput);
        if (startupInfo.StandardError is not null) streams.Add(startupInfo.StandardError);
        startupInfo.StandardInput = startupInfo.StandardOutput = startupInfo.StandardError = null;
        foreach (var stream in streams) stream.Dispose();
    }

    [NssmUpstreamFunction("src/io.cpp", 432, "void cleanup_loggers(nssm_service_t *service)", "NssmIoTests.cleanup_loggers_waits_for_both_pumps")]
    public static async Task cleanup_loggers(IEnumerable<Task<uint>>? loggerThreads, uint deadline = 1500)
    {
        if (loggerThreads is null) return;
        foreach (var thread in loggerThreads)
        {
            try { await thread.WaitAsync(TimeSpan.FromMilliseconds(deadline)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    [NssmUpstreamFunction("src/io.cpp", 456, "static int try_read(logger_t *logger, void *address, unsigned long bufsize, unsigned long *in, int *complained)", "NssmIoTests.retry_read_write_and_timestamp_preserve_bytes")]
    public static int try_read(NssmLogger logger, byte[] address, int bufferSize, out uint read, ref int complained)
    {
        read = 0;
        for (var tries = 0; tries < 5; tries++)
        {
            try { read = checked((uint)logger.ReadHandle.Read(address, 0, bufferSize)); return read == 0 ? -1 : 0; }
            catch (IOException exception)
            {
                var error = exception.HResult & 0xffff;
                if (error == 109) return -1;
                if (error == 1816) { Thread.Sleep(2000 + tries * 3000); continue; }
                if (error == 995) { complained |= ComplainedRead; return 1; }
                complained |= ComplainedRead;
                return -1;
            }
        }
        complained |= ComplainedRead;
        return 1;
    }

    [NssmUpstreamFunction("src/io.cpp", 499, "static int try_write(logger_t *logger, void *address, unsigned long bufsize, unsigned long *out, int *complained)", "NssmIoTests.retry_read_write_and_timestamp_preserve_bytes")]
    public static int try_write(NssmLogger logger, ReadOnlySpan<byte> address, out uint written, ref int complained)
    {
        written = 0;
        for (var tries = 0; tries < 5; tries++)
        {
            try { logger.WriteHandle.Write(address); written = checked((uint)address.Length); return 0; }
            catch (IOException exception)
            {
                var error = exception.HResult & 0xffff;
                if (error is 1816 or 112) { Thread.Sleep(2000 + tries * 3000); continue; }
                complained |= ComplainedWrite;
                return error == 109 ? -1 : 1;
            }
        }
        complained |= ComplainedWrite;
        return 1;
    }

    [NssmUpstreamFunction("src/io.cpp", 538, "static inline int write_timestamp(logger_t *logger, unsigned long charsize, unsigned long *out, int *complained)", "NssmIoTests.character_width_bom_and_timestamp_match_upstream")]
    public static int write_timestamp(NssmLogger logger, uint characterSize, out uint written, ref int complained)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff: ", System.Globalization.CultureInfo.InvariantCulture);
        var bytes = characterSize == 1 ? Encoding.UTF8.GetBytes(timestamp) : Encoding.Unicode.GetBytes(timestamp);
        return try_write(logger, bytes, out written, ref complained);
    }

    [NssmUpstreamFunction("src/io.cpp", 555, "static int write_with_timestamp(logger_t *logger, void *address, unsigned long bufsize, unsigned long *out, int *complained, unsigned long charsize)", "NssmIoTests.retry_read_write_and_timestamp_preserve_bytes")]
    public static int write_with_timestamp(NssmLogger logger, ReadOnlySpan<byte> address, out uint written, ref int complained, uint characterSize)
    {
        written = 0;
        if (!logger.TimestampLog) return try_write(logger, address, out written, ref complained);
        var timestampWritten = 0u;
        if (logger.LineLength == 0)
        {
            _ = write_timestamp(logger, characterSize, out timestampWritten, ref complained);
            logger.LineLength += timestampWritten;
            written += timestampWritten;
        }

        var offset = 0;
        var ret = 0;
        for (var index = 0; index < address.Length; index++)
        {
            if (address[index] != (byte)'\n') continue;
            ret = try_write(logger, address.Slice(offset, index - offset + 1), out var lineWritten, ref complained);
            written += lineWritten;
            logger.LineLength = 0;
            offset = index + 1;
            if (offset >= address.Length) continue;
            _ = write_timestamp(logger, characterSize, out timestampWritten, ref complained);
            logger.LineLength += timestampWritten;
            written += timestampWritten;
        }

        if (offset < address.Length)
        {
            ret = try_write(logger, address[offset..], out var remainderWritten, ref complained);
            written += remainderWritten;
            logger.LineLength += remainderWritten;
        }
        return ret;
    }

    [NssmUpstreamFunction("src/io.cpp", 601, "unsigned long WINAPI log_and_rotate(void *arg)", "NssmIoTests.logging_thread_copies_and_timestamps_data")]
    public static uint log_and_rotate(NssmLogger? logger)
    {
        if (logger is null) return 1;
        ulong size;
        try { size = unchecked((ulong)logger.WriteHandle.Length); }
        catch { size = 0; }
        var buffer = new byte[1024];
        uint characterSize = 0;
        var complained = 0;
        while (true)
        {
            var readStatus = try_read(logger, buffer, buffer.Length, out var input, ref complained);
            if (readStatus < 0) { logger.ReadHandle.Dispose(); logger.WriteHandle.Dispose(); return 2; }
            if (readStatus != 0) continue;
            var payload = buffer.AsSpan(0, checked((int)input));
            while (logger.RotateOnline.Value == RotateOnlineAsap || (logger.SizeThreshold != 0 && size != 0 && size + unchecked((uint)payload.Length) >= logger.SizeThreshold))
            {
                if (characterSize == 0) characterSize = guess_charsize(payload);
                var newline = payload.IndexOf((byte)'\n');
                if (newline < 0) break;
                var split = Math.Min(payload.Length, newline + checked((int)characterSize));
                if (try_write(logger, payload[..split], out var prefixWritten, ref complained) < 0)
                {
                    logger.ReadHandle.Dispose();
                    logger.WriteHandle.Dispose();
                    return 3;
                }
                size += prefixWritten;
                logger.RotateOnline.Value = RotateOnline;
                if (logger.CopyAndTruncate) logger.WriteHandle.Flush(true);
                logger.WriteHandle.Dispose();
                var rotated = rotated_filename(logger.Path, DateTime.UtcNow);
                var rotatedOk = RotateOnlineFile(logger.Path, rotated, logger.RotateDelay, logger.CopyAndTruncate);
                if (!rotatedOk)
                {
                    var rotateError = Marshal.GetLastPInvokeError();
                    if (rotateError != 2 && (complained & ComplainedRotate) == 0)
                        NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_ROTATE_FILE_FAILED"), logger.ServiceName, logger.Path,
                            logger.CopyAndTruncate ? "CopyFile()" : "MoveFile()", rotated, NssmEvent.error_string(unchecked((uint)rotateError)));
                    complained |= ComplainedRotate;
                    logger.Disposition = 4;
                }
                else
                {
                    NssmEvent.log_event(4, NssmEvent.message_id("NSSM_EVENT_ROTATED"), logger.ServiceName, logger.Path, rotated);
                    size = 0;
                }
                var reopened = write_to_file(logger.Path, logger.Sharing, logger.Disposition, logger.Flags);
                if (reopened is null)
                {
                    logger.ReadHandle.Dispose();
                    return 4;
                }
                logger.WriteHandle = reopened;
                payload = payload[split..];
                if (payload.Length == 0) break;
            }
            if ((size == 0 || logger.TimestampLog) && characterSize == 0) characterSize = guess_charsize(payload);
            if (size == 0 && characterSize == 2) { write_bom(logger, out var bomWritten); size += bomWritten; }
            if (payload.Length == 0) continue;
            if (write_with_timestamp(logger, payload, out var output, ref complained, characterSize) < 0) return 3;
            size += output;
        }
    }

    private static uint ReadNumber(RegistryKey key, string name, uint defaultValue, int errorCode, out int error)
    {
        var ret = NssmRegistry.get_number(key, name, out var number, false);
        error = ret == -2 ? errorCode : 0;
        return ret == 1 ? number : defaultValue;
    }

    private static FileStream DuplicateStream(FileStream stream)
    {
        if (dup_handle(stream.SafeFileHandle, out var duplicate, "stream", "stream") != 0 || duplicate is null) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new FileStream(duplicate, FileAccess.Write, bufferSize: 1, isAsync: false);
    }

    private static bool RotateOnlineFile(string path, string rotated, uint delay, bool copyAndTruncate)
    {
        if (!copyAndTruncate) return MoveFile(path, rotated);
        if (!CopyFile(path, rotated, true)) return false;
        using var file = write_to_file(path, 3, 4, 128);
        Sleep(delay);
        if (file is not null)
        {
            _ = SetFilePointerEx(file.SafeFileHandle, 0, out _, 0);
            _ = SetEndOfFile(file.SafeFileHandle);
        }
        return true;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEndOfFile(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFile(string existingFile, string newFile, [MarshalAs(UnmanagedType.Bool)] bool failIfExists);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFile(string existingFile, string newFile);

    [DllImport("kernel32.dll")]
    private static extern void Sleep(uint milliseconds);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTextUnicode(byte[] buffer, int size, IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }
}
