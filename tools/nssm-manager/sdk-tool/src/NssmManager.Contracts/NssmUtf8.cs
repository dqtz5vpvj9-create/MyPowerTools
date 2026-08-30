using System.Text;

namespace NssmManager.Contracts;

/// <summary>
/// Direct managed translation of utf8.cpp.  Returned byte buffers include the
/// trailing NUL just like the HeapAlloc() buffers returned by upstream NSSM;
/// reported lengths exclude that terminator.
/// </summary>
public static class NssmUtf8
{
    private static Encoding? _savedOutputEncoding;
    public static bool IsSetup => _savedOutputEncoding is not null;

    [NssmUpstreamFunction("src/utf8.cpp", 5, "void setup_utf8()", "NssmUtf8Tests.setup_utf8_round_trips_console_encoding")]
    public static void setup_utf8()
    {
        _savedOutputEncoding ??= Console.OutputEncoding;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    [NssmUpstreamFunction("src/utf8.cpp", 18, "void unsetup_utf8()", "NssmUtf8Tests.setup_utf8_round_trips_console_encoding")]
    public static void unsetup_utf8()
    {
        if (_savedOutputEncoding is null) return;
        Console.OutputEncoding = _savedOutputEncoding;
        _savedOutputEncoding = null;
    }

    [NssmUpstreamFunction("src/utf8.cpp", 42, "int to_utf8(const wchar_t *utf16, char **utf8, unsigned long *utf8len)", "NssmUtf8Tests.to_utf8_from_utf16_returns_nul_terminated_copy")]
    public static int to_utf8(string utf16, out byte[]? utf8, out uint utf8len)
    {
        utf8 = null;
        utf8len = 0;

        try
        {
            var payload = Encoding.UTF8.GetBytes(utf16);
            utf8 = new byte[payload.Length + 1];
            payload.CopyTo(utf8, 0);
            utf8len = checked((uint)payload.Length);
            return 0;
        }
        catch (EncoderFallbackException)
        {
            return 1;
        }
        catch (OutOfMemoryException)
        {
            utf8 = null;
            utf8len = 0;
            return 2;
        }
    }

    [NssmUpstreamFunction("src/utf8.cpp", 62, "int to_utf8(const char *ansi, char **utf8, unsigned long *utf8len)", "NssmUtf8Tests.to_utf8_from_bytes_copies_through_first_nul")]
    public static int to_utf8(ReadOnlySpan<byte> ansi, out byte[]? utf8, out uint utf8len)
    {
        utf8 = null;
        utf8len = 0;

        try
        {
            var terminator = ansi.IndexOf((byte)0);
            var length = terminator < 0 ? ansi.Length : terminator;
            utf8 = new byte[length + 1];
            ansi[..length].CopyTo(utf8);
            utf8len = checked((uint)length);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            utf8 = null;
            utf8len = 0;
            return 2;
        }
    }

    [NssmUpstreamFunction("src/utf8.cpp", 77, "int to_utf16(const char *utf8, wchar_t **utf16, unsigned long *utf16len)", "NssmUtf8Tests.to_utf16_from_utf8_stops_at_nul")]
    public static int to_utf16(ReadOnlySpan<byte> utf8, out string? utf16, out uint utf16len)
    {
        utf16 = null;
        utf16len = 0;

        try
        {
            var terminator = utf8.IndexOf((byte)0);
            var payload = terminator < 0 ? utf8 : utf8[..terminator];
            utf16 = Encoding.UTF8.GetString(payload);
            utf16len = checked((uint)utf16.Length);
            return 0;
        }
        catch (DecoderFallbackException)
        {
            return 1;
        }
        catch (OutOfMemoryException)
        {
            utf16 = null;
            utf16len = 0;
            return 2;
        }
    }

    [NssmUpstreamFunction("src/utf8.cpp", 97, "int to_utf16(const wchar_t *unicode, wchar_t **utf16, unsigned long *utf16len)", "NssmUtf8Tests.to_utf16_from_utf16_returns_distinct_copy")]
    public static int to_utf16(string unicode, out string? utf16, out uint utf16len)
    {
        utf16 = null;
        utf16len = 0;

        try
        {
            utf16 = new string(unicode.AsSpan());
            utf16len = checked((uint)utf16.Length);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            utf16 = null;
            utf16len = 0;
            return 2;
        }
    }

    [NssmUpstreamFunction("src/utf8.cpp", 112, "int from_utf8(const char *utf8, TCHAR **buffer, unsigned long *buflen)", "NssmUtf8Tests.from_utf8_uses_unicode_build_contract")]
    public static int from_utf8(ReadOnlySpan<byte> utf8, out string? buffer, out uint buflen) =>
        to_utf16(utf8, out buffer, out buflen);

    [NssmUpstreamFunction("src/utf8.cpp", 120, "int from_utf16(const wchar_t *utf16, TCHAR **buffer, unsigned long *buflen)", "NssmUtf8Tests.from_utf16_uses_unicode_build_contract")]
    public static int from_utf16(string utf16, out string? buffer, out uint buflen) =>
        to_utf16(utf16, out buffer, out buflen);
}
