namespace FileTransfer.Core;

internal static class UrlSafeBase64
{
    public static string EncodeToString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] DecodeFromChars(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return [];
        }

        var unpadded = StripOptionalPadding(chars);
        for (var i = 0; i < unpadded.Length; i++)
        {
            if (!IsBase64UrlCharacter(unpadded[i]))
            {
                throw new FormatException("Input contains characters outside the Base64Url alphabet.");
            }
        }

        var padCount = (unpadded.Length % 4) switch
        {
            0 => 0,
            2 => 2,
            3 => 1,
            _ => throw new FormatException("Invalid Base64Url length.")
        };

        var paddedLength = unpadded.Length + padCount;
        var standardChars = new char[paddedLength];
        for (var i = 0; i < unpadded.Length; i++)
        {
            standardChars[i] = unpadded[i] switch
            {
                '-' => '+',
                '_' => '/',
                var c => c
            };
        }

        for (var i = unpadded.Length; i < paddedLength; i++)
        {
            standardChars[i] = '=';
        }

        return Convert.FromBase64CharArray(standardChars, 0, paddedLength);
    }

    private static ReadOnlySpan<char> StripOptionalPadding(ReadOnlySpan<char> chars)
    {
        var equalsIndex = chars.IndexOf('=');
        if (equalsIndex < 0)
        {
            return chars;
        }

        if (chars.Length % 4 != 0)
        {
            throw new FormatException("Padded Base64Url input length must be a multiple of 4.");
        }

        var padLength = chars.Length - equalsIndex;
        if (padLength is < 1 or > 2)
        {
            throw new FormatException("Base64Url padding may contain at most two '=' characters.");
        }

        for (var i = equalsIndex; i < chars.Length; i++)
        {
            if (chars[i] != '=')
            {
                throw new FormatException("Base64Url padding '=' may only appear at the end.");
            }
        }

        return chars[..equalsIndex];
    }

    private static bool IsBase64UrlCharacter(char c)
    {
        return c is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '0' and <= '9')
            or '-'
            or '_';
    }
}
