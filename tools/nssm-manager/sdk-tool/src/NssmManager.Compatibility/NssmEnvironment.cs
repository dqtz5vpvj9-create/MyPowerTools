using System.Collections;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using NssmManager.Contracts;

namespace NssmManager.Compatibility;

/// <summary>Direct managed translation of env.cpp.</summary>
public static class NssmEnvironment
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ErrorInvalidParameter = 87;

    [NssmUpstreamFunction("src/env.cpp", 19, "size_t environment_length(TCHAR *env)", "NssmEnvironmentTests.environment_length_includes_double_nul")]
    public static nuint environment_length(string env)
    {
        for (var index = 0; index < env.Length; index++)
        {
            if (env[index] == '\0' && CharacterAt(env, index + 1) == '\0') return checked((nuint)(index + 2));
        }
        return checked((nuint)(env.Length + 2));
    }

    [NssmUpstreamFunction("src/env.cpp", 37, "TCHAR *copy_environment_block(TCHAR *env)", "NssmEnvironmentTests.copy_environment_block_uses_double_nul_length")]
    public static string? copy_environment_block(string? env)
    {
        if (env is null) return null;
        var length = checked((uint)environment_length(env));
        var normalized = EnsureCapacity(env, checked((int)length));
        return NssmDoubleNull.copy_double_null(normalized, length, out var copy) == 0 ? copy : null;
    }

    [NssmUpstreamFunction("src/env.cpp", 47, "TCHAR *useful_environment(TCHAR *rawenv)", "NssmEnvironmentTests.useful_environment_skips_drive_entries")]
    public static string? useful_environment(string? rawenv)
    {
        if (rawenv is null) return null;
        var offset = 0;
        while (offset < rawenv.Length && rawenv[offset] == '=')
        {
            var end = rawenv.IndexOf('\0', offset);
            if (end < 0) return "\0\0";
            offset = end + 1;
        }
        return rawenv[offset..];
    }

    [NssmUpstreamFunction("src/env.cpp", 61, "TCHAR *expand_environment_string(TCHAR *string)", "NssmEnvironmentTests.expand_environment_string_matches_windows_percent_syntax")]
    public static string? expand_environment_string(string value)
    {
        try
        {
            return Environment.ExpandEnvironmentVariables(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [NssmUpstreamFunction("src/env.cpp", 90, "static int set_environment_block(TCHAR *env, bool set)", "NssmEnvironmentTests.set_environment_block_expands_values_and_counts_failures")]
    public static int set_environment_block(string? env, bool set)
    {
        var failures = 0;
        foreach (var entry in NssmDoubleNull.ToStrings(env))
        {
            var delimiter = entry.IndexOf('=');
            if (delimiter < 0) continue;
            var name = entry[..delimiter];
            var rawValue = entry[(delimiter + 1)..];
            try
            {
                Environment.SetEnvironmentVariable(name, set ? expand_environment_string(rawValue) ?? rawValue : null, EnvironmentVariableTarget.Process);
            }
            catch (ArgumentException)
            {
                failures++;
            }
            catch (SecurityException)
            {
                failures++;
            }
        }
        return failures;
    }

    [NssmUpstreamFunction("src/env.cpp", 119, "int set_environment_block(TCHAR *env)", "NssmEnvironmentTests.set_environment_block_expands_values_and_counts_failures")]
    public static int set_environment_block(string? env) => set_environment_block(env, true);

    [NssmUpstreamFunction("src/env.cpp", 123, "static int unset_environment_block(TCHAR *env)", "NssmEnvironmentTests.unset_environment_block_removes_named_values")]
    public static int unset_environment_block(string? env) => set_environment_block(env, false);

    [NssmUpstreamFunction("src/env.cpp", 128, "int clear_environment()", "NssmEnvironmentProcessTests.clear_environment_preserves_drive_pseudo_variables")]
    public static int clear_environment()
    {
        var values = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process);
        var failures = 0;
        foreach (DictionaryEntry pair in values)
        {
            var name = pair.Key?.ToString();
            if (string.IsNullOrEmpty(name) || name.StartsWith('=')) continue;
            try
            {
                Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            }
            catch (ArgumentException)
            {
                failures++;
            }
            catch (SecurityException)
            {
                failures++;
            }
        }
        return failures;
    }

    [NssmUpstreamFunction("src/env.cpp", 140, "int duplicate_environment(TCHAR *rawenv)", "NssmEnvironmentProcessTests.duplicate_environment_replaces_process_environment")]
    public static int duplicate_environment(string? rawenv)
    {
        var result = clear_environment();
        result += set_environment_block(useful_environment(rawenv));
        return result;
    }

    [NssmUpstreamFunction("src/env.cpp", 153, "int test_environment(TCHAR *env)", "NssmEnvironmentTests.test_environment_rejects_malformed_block")]
    public static int test_environment(string? env)
    {
        if (!HasValidShape(env)) return 1;
        if (!OperatingSystem.IsWindows()) return 0;

        var startup = new StartupInfo { Cb = checked((uint)Marshal.SizeOf<StartupInfo>()) };
        var command = new StringBuilder($"\"{Environment.ProcessPath}\"");
        var environment = Marshal.StringToHGlobalUni(env);
        try
        {
            if (CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateUnicodeEnvironment, environment, null, ref startup, out var process))
            {
                TerminateProcess(process.Process, 0);
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
                return 0;
            }
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? 1 : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    [NssmUpstreamFunction("src/env.cpp", 188, "void duplicate_environment_strings(TCHAR *env)", "NssmEnvironmentProcessTests.duplicate_environment_strings_does_not_mutate_input")]
    public static void duplicate_environment_strings(string? env)
    {
        var copy = copy_environment_block(env);
        if (copy is not null) duplicate_environment(copy);
    }

    [NssmUpstreamFunction("src/env.cpp", 197, "TCHAR *copy_environment()", "NssmEnvironmentTests.copy_environment_is_double_nul_terminated")]
    public static string? copy_environment()
    {
        try
        {
            var values = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process)
                .Cast<DictionaryEntry>()
                .Select(pair => $"{pair.Key}={pair.Value}")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return NssmDoubleNull.FromStrings(values);
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    [NssmUpstreamFunction("src/env.cpp", 211, "int append_to_environment_block(TCHAR *env, unsigned long envlen, TCHAR *string, TCHAR **newenv, unsigned long *newlen)", "NssmEnvironmentTests.append_to_environment_block_replaces_key_case_insensitively")]
    public static int append_to_environment_block(string? env, uint envlen, string? value, out string? newenv, out uint newlen)
    {
        var keyLength = 0;
        if (!string.IsNullOrEmpty(value))
        {
            while (keyLength < value.Length)
            {
                if (value[keyLength++] == '=') break;
            }
        }
        return NssmDoubleNull.append_to_double_null(env, envlen, out newenv, out newlen, value, keyLength, false);
    }

    [NssmUpstreamFunction("src/env.cpp", 234, "int remove_from_environment_block(TCHAR *env, unsigned long envlen, TCHAR *string, TCHAR **newenv, unsigned long *newlen)", "NssmEnvironmentTests.remove_from_environment_block_honours_optional_value")]
    public static int remove_from_environment_block(string? env, uint envlen, string? value, out string? newenv, out uint newlen)
    {
        newenv = null;
        newlen = 0;
        if (string.IsNullOrEmpty(value) || value[0] == '=') return 1;

        var delimiter = value.IndexOf('=');
        var key = delimiter < 0 ? value + "=" : value;
        return NssmDoubleNull.remove_from_double_null(env, envlen, out newenv, out newlen, key, key.Length, false);
    }

    private static bool HasValidShape(string? env)
    {
        if (env is null || env.Length < 2 || env[^1] != '\0' || env[^2] != '\0') return false;
        foreach (var entry in NssmDoubleNull.ToStrings(env))
        {
            var delimiter = entry.IndexOf('=');
            if (delimiter <= 0 && !entry.StartsWith('=')) return false;
        }
        return true;
    }

    private static string EnsureCapacity(string value, int length) => value.Length >= length ? value[..length] : value.PadRight(length, '\0');
    private static char CharacterAt(string value, int index) => index >= 0 && index < value.Length ? value[index] : '\0';

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
