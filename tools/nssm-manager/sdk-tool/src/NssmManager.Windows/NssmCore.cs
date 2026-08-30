using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using NssmManager.Contracts;

namespace NssmManager.Windows;

/// <summary>Direct translation of the non-dispatch helpers in nssm.cpp.</summary>
public static class NssmCore
{
    private static readonly string UnquotedImagePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
    private static readonly string ImagePath = quote(UnquotedImagePath, int.MaxValue, out var quoted) == 0 ? quoted : UnquotedImagePath;
    private static readonly string ImageArgv0 = Environment.GetCommandLineArgs().FirstOrDefault() ?? UnquotedImagePath;

    [NssmUpstreamFunction("src/nssm.cpp", 11, "void nssm_exit(int status)", "NssmCoreTests.nssm_exit_cleanup_is_exercised_in_child_process")]
    public static void nssm_exit(int status)
    {
        NssmImports.free_imports();
        NssmUtf8.unsetup_utf8();
        Environment.Exit(status);
    }

    [NssmUpstreamFunction("src/nssm.cpp", 18, "int str_equiv(const TCHAR *a, const TCHAR *b)", "NssmCoreTests.str_equiv_is_case_insensitive_and_length_exact")]
    public static int str_equiv(string a, string b) =>
        a.Length == b.Length && string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    [NssmUpstreamFunction("src/nssm.cpp", 26, "int str_number(const TCHAR *string, unsigned long *number, TCHAR **bogus)", "NssmCoreTests.str_number_matches_tcstoul_base_zero")]
    public static int str_number(string? value, out uint number, out int bogus)
    {
        number = 0;
        bogus = 0;
        if (value is null) return 1;

        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
        var conversionStart = index;
        var negative = false;
        if (index < value.Length && (value[index] == '+' || value[index] == '-'))
        {
            negative = value[index] == '-';
            index++;
        }

        var numberBase = 10;
        if (index < value.Length && value[index] == '0')
        {
            numberBase = 8;
            if (index + 1 < value.Length && (value[index + 1] == 'x' || value[index + 1] == 'X'))
            {
                numberBase = 16;
                index += 2;
            }
        }

        var digits = 0;
        ulong accumulator = 0;
        while (index < value.Length)
        {
            var digit = Digit(value[index]);
            if (digit < 0 || digit >= numberBase) break;
            accumulator = unchecked(accumulator * (uint)numberBase + (uint)digit);
            index++;
            digits++;
        }

        if (digits == 0)
        {
            // A single leading zero is a valid octal conversion.
            if (numberBase == 8 && index < value.Length && value[index] == '0')
            {
                index++;
                digits = 1;
            }
            else
            {
                bogus = conversionStart;
                return bogus < value.Length ? 2 : 0;
            }
        }

        var converted = unchecked((uint)accumulator);
        number = negative ? unchecked(0u - converted) : converted;
        bogus = index;
        return bogus == value.Length ? 0 : 2;
    }

    [NssmUpstreamFunction("src/nssm.cpp", 36, "static bool is_version(const TCHAR *s)", "NssmCoreTests.is_version_matches_all_upstream_switches")]
    public static bool is_version(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var candidate = value;
        if (candidate[0] == '/') candidate = candidate[1..];
        else if (candidate[0] == '-')
        {
            candidate = candidate[1..];
            if (candidate.StartsWith('-')) candidate = candidate[1..];
            else if (str_equiv(candidate, "v") != 0) return true;
        }
        return str_equiv(candidate, "version") != 0;
    }

    [NssmUpstreamFunction("src/nssm.cpp", 50, "int str_number(const TCHAR *string, unsigned long *number)", "NssmCoreTests.str_number_matches_tcstoul_base_zero")]
    public static int str_number(string? value, out uint number) => str_number(value, out number, out _);

    [NssmUpstreamFunction("src/nssm.cpp", 56, "static bool needs_escape(const TCHAR c)", "NssmCoreTests.quote_matches_upstream_meta_character_escaping")]
    public static bool needs_escape(char value) => value is '"' or '&' or '%' or '^' or '<' or '>' or '|';

    [NssmUpstreamFunction("src/nssm.cpp", 68, "static bool needs_quote(const TCHAR c)", "NssmCoreTests.quote_matches_upstream_meta_character_escaping")]
    public static bool needs_quote(char value) => value is ' ' or '\t' or '\n' or '\v' or '"' or '*' || needs_escape(value);

    [NssmUpstreamFunction("src/nssm.cpp", 80, "int quote(const TCHAR *unquoted, TCHAR *buffer, size_t buflen)", "NssmCoreTests.quote_matches_upstream_meta_character_escaping")]
    public static int quote(string unquoted, int buflen, out string buffer)
    {
        buffer = string.Empty;
        if (unquoted.Length > buflen - 1) return 1;

        var escape = false;
        var quotes = false;
        foreach (var character in unquoted)
        {
            if (needs_escape(character))
            {
                escape = true;
                quotes = true;
                break;
            }
            if (needs_quote(character)) quotes = true;
        }

        if (!quotes)
        {
            buffer = unquoted;
            return 0;
        }

        var result = new StringBuilder(unquoted.Length + 8);
        if (escape) result.Append('^');
        result.Append('"');

        for (var i = 0; ; i++)
        {
            var slashes = 0;
            while (i != unquoted.Length && unquoted[i] == '\\')
            {
                i++;
                slashes++;
            }

            if (i == unquoted.Length)
            {
                AppendSlashes(result, slashes * 2, escape);
                break;
            }

            if (unquoted[i] == '"')
            {
                AppendSlashes(result, slashes * 2 + 1, escape);
                if (escape && needs_escape(unquoted[i])) result.Append('^');
                result.Append(unquoted[i]);
            }
            else
            {
                AppendSlashes(result, slashes, escape);
                if (escape && needs_escape(unquoted[i])) result.Append('^');
                result.Append(unquoted[i]);
            }
        }

        if (escape) result.Append('^');
        result.Append('"');
        if (result.Length > buflen - 1) return 1;
        buffer = result.ToString();
        return 0;
    }

    [NssmUpstreamFunction("src/nssm.cpp", 165, "void strip_basename(TCHAR *buffer)", "NssmCoreTests.strip_basename_preserves_drive_root")]
    public static string strip_basename(string buffer)
    {
        var index = buffer.Length;
        while (index > 0 && (index >= buffer.Length || buffer[index] is not ('\\' or '/'))) index--;
        if (index > 0 && buffer[index - 1] == ':') index++;
        return buffer[..index];
    }

    [NssmUpstreamFunction("src/nssm.cpp", 175, "int usage(int ret)", "NssmCliDifferentialTests.usage_matches_upstream")]
    public static int usage(int ret, TextWriter? error = null)
    {
        NssmEvent.print_message(
            error ?? Console.Error,
            NssmEvent.message_id("NSSM_MESSAGE_USAGE"),
            "2.24-101-g897c7ad",
            "64-bit",
            "2017-04-26");
        return ret;
    }

    [NssmUpstreamFunction("src/nssm.cpp", 181, "void check_admin()", "NssmCoreTests.check_admin_matches_token_membership")]
    public static bool check_admin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [NssmUpstreamFunction("src/nssm.cpp", 192, "static int elevate(int argc, TCHAR **argv, unsigned long message)", "NssmCliDifferentialTests.elevated_mutations_preserve_exit_codes")]
    public static int elevate(IReadOnlyList<string> argv, Func<IReadOnlyList<string>, int> broker, TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return broker(argv);
    }

    [NssmUpstreamFunction("src/nssm.cpp", 223, "int num_cpus()", "NssmCoreTests.num_cpus_matches_system_affinity_high_bit")]
    public static int num_cpus()
    {
        if (!OperatingSystem.IsWindows()) return Math.Min(Environment.ProcessorCount, 64);
        if (!GetProcessAffinityMask(GetCurrentProcess(), out _, out var systemAffinity)) return 64;
        var count = 0;
        while (count < IntPtr.Size * 8 && (systemAffinity.ToUInt64() & (1UL << count)) != 0) count++;
        return count;
    }

    [NssmUpstreamFunction("src/nssm.cpp", 230, "const TCHAR *nssm_unquoted_imagepath()", "NssmCoreTests.image_paths_have_upstream_quoting_contract")]
    public static string nssm_unquoted_imagepath() => UnquotedImagePath;

    [NssmUpstreamFunction("src/nssm.cpp", 234, "const TCHAR *nssm_imagepath()", "NssmCoreTests.image_paths_have_upstream_quoting_contract")]
    public static string nssm_imagepath() => ImagePath;

    [NssmUpstreamFunction("src/nssm.cpp", 238, "const TCHAR *nssm_exe()", "NssmCoreTests.image_paths_have_upstream_quoting_contract")]
    public static string nssm_exe() => ImageArgv0;

    private static int Digit(char value)
    {
        if (value is >= '0' and <= '9') return value - '0';
        if (value is >= 'a' and <= 'z') return value - 'a' + 10;
        if (value is >= 'A' and <= 'Z') return value - 'A' + 10;
        return -1;
    }

    private static void AppendSlashes(StringBuilder target, int count, bool escape)
    {
        for (var index = 0; index < count; index++)
        {
            if (escape) target.Append('^');
            target.Append('\\');
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processAffinityMask, out UIntPtr systemAffinityMask);
}
