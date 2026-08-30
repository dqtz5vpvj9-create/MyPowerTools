using NssmManager.Contracts;

namespace NssmManager.Compatibility;

/// <summary>
/// Managed representation of the double-NUL buffers used by registry.cpp.
/// The string length is the TCHAR count and includes both terminators.
/// </summary>
public static class NssmDoubleNull
{
    [NssmUpstreamFunction("src/registry.cpp", 418, "int format_double_null(TCHAR *dn, unsigned long dnlen, TCHAR **formatted, unsigned long *newlen)", "NssmDoubleNullTests.format_double_null_matches_upstream_layout")]
    public static int format_double_null(string? dn, uint dnlen, out string? formatted, out uint newlen)
    {
        newlen = dnlen;
        if (newlen == 0)
        {
            formatted = null;
            return 0;
        }

        var source = Slice(dn, dnlen);
        var extra = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\0' && CharacterAt(source, index + 1) != '\0') extra++;
        }

        newlen = checked((uint)(source.Length + extra));
        try
        {
            var target = new char[newlen];
            for (int sourceIndex = 0, targetIndex = 0; sourceIndex < source.Length; sourceIndex++, targetIndex++)
            {
                target[targetIndex] = source[sourceIndex];
                if (source[sourceIndex] == '\0' && CharacterAt(source, sourceIndex + 1) != '\0')
                {
                    target[targetIndex] = '\r';
                    target[++targetIndex] = '\n';
                }
            }
            formatted = new string(target);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            formatted = null;
            newlen = 0;
            return 1;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 450, "int unformat_double_null(TCHAR *formatted, unsigned long formattedlen, TCHAR **dn, unsigned long *newlen)", "NssmDoubleNullTests.unformat_double_null_strips_cr_and_blank_lines")]
    public static int unformat_double_null(string? formatted, uint formattedlen, out string? dn, out uint newlen)
    {
        newlen = 0;
        var source = Slice(formatted, formattedlen);
        var terminator = source.IndexOf('\0');
        if (terminator >= 0) source = source[..terminator];
        if (source.Length == 0)
        {
            dn = null;
            return 0;
        }

        // Upstream removes empty CRLF rows before converting line feeds.
        var rows = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var payload = string.Join('\0', rows.Select(row => row.Replace("\r", "", StringComparison.Ordinal)));
        try
        {
            dn = payload + "\0\0";
            newlen = checked((uint)dn.Length);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            dn = null;
            newlen = 0;
            return 1;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 506, "int copy_double_null(TCHAR *dn, unsigned long dnlen, TCHAR **newdn)", "NssmDoubleNullTests.copy_double_null_honours_explicit_length")]
    public static int copy_double_null(string? dn, uint dnlen, out string? newdn)
    {
        newdn = null;
        if (dn is null) return 0;
        try
        {
            newdn = new string(Slice(dn, dnlen).AsSpan());
            return 0;
        }
        catch (OutOfMemoryException)
        {
            return 2;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 530, "int append_to_double_null(TCHAR *dn, unsigned long dnlen, TCHAR **newdn, unsigned long *newlen, TCHAR *append, size_t keylen, bool case_sensitive)", "NssmDoubleNullTests.append_to_double_null_replaces_matching_key")]
    public static int append_to_double_null(
        string? dn,
        uint dnlen,
        out string? newdn,
        out uint newlen,
        string? append,
        int keylen,
        bool caseSensitive)
    {
        newlen = 0;
        if (string.IsNullOrEmpty(append)) return copy_double_null(dn, dnlen, out newdn);

        try
        {
            if (keylen <= 0 || keylen > append.Length) keylen = append.Length;
            var key = append[..keylen];
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var entries = Entries(Slice(dn, dnlen));
            var replaced = false;

            for (var index = 0; index < entries.Count; index++)
            {
                if (!entries[index].StartsWith(key, comparison)) continue;
                entries[index] = append;
                replaced = true;
            }
            if (!replaced) entries.Add(append);

            newdn = Build(entries);
            newlen = checked((uint)newdn.Length);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            newdn = null;
            newlen = 0;
            return 2;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 601, "int remove_from_double_null(TCHAR *dn, unsigned long dnlen, TCHAR **newdn, unsigned long *newlen, TCHAR *remove, size_t keylen, bool case_sensitive)", "NssmDoubleNullTests.remove_from_double_null_matches_prefix_contract")]
    public static int remove_from_double_null(
        string? dn,
        uint dnlen,
        out string? newdn,
        out uint newlen,
        string? remove,
        int keylen,
        bool caseSensitive)
    {
        newlen = 0;
        if (string.IsNullOrEmpty(remove)) return copy_double_null(dn, dnlen, out newdn);

        try
        {
            if (keylen <= 0 || keylen > remove.Length) keylen = remove.Length;
            var key = remove[..keylen];
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var entries = Entries(Slice(dn, dnlen));
            entries.RemoveAll(entry => entry.StartsWith(key, comparison));
            newdn = Build(entries);
            newlen = checked((uint)newdn.Length);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            newdn = null;
            newlen = 0;
            return 2;
        }
    }

    public static string FromStrings(IEnumerable<string> values) => Build(values.ToList());

    public static string[] ToStrings(string? value, uint? length = null) =>
        Entries(Slice(value, length ?? checked((uint)(value?.Length ?? 0)))).ToArray();

    private static string Slice(string? value, uint length)
    {
        if (value is null || length == 0) return string.Empty;
        var count = checked((int)Math.Min(length, checked((uint)value.Length)));
        return value[..count];
    }

    private static char CharacterAt(string value, int index) => index >= 0 && index < value.Length ? value[index] : '\0';

    private static List<string> Entries(string block)
    {
        var entries = new List<string>();
        var offset = 0;
        while (offset < block.Length && block[offset] != '\0')
        {
            var end = block.IndexOf('\0', offset);
            if (end < 0) end = block.Length;
            entries.Add(block[offset..end]);
            offset = end + 1;
        }
        return entries;
    }

    private static string Build(IReadOnlyList<string> entries) =>
        entries.Count == 0 ? "\0\0" : string.Join('\0', entries) + "\0\0";
}
