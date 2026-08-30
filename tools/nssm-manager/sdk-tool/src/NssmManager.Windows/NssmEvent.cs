using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NssmManager.Contracts;

namespace NssmManager.Windows;

/// <summary>Direct managed translation of event.cpp.</summary>
public static class NssmEvent
{
    private const string EventSource = "nssm";
    private const int ErrorBufferSize = 65535;
    private const uint FormatMessageFromSystem = 0x00001000;
    private const uint FormatMessageIgnoreInserts = 0x00000200;
    private const uint MbOk = 0x00000000;
    private const uint MbIconExclamation = 0x00000030;
    private static readonly Lazy<MessageCatalog> Catalog = new(MessageCatalog.Load, LazyThreadSafetyMode.ExecutionAndPublication);

    [NssmUpstreamFunction("src/event.cpp", 9, "TCHAR *error_string(unsigned long error)", "NssmEventTests.error_string_formats_win32_error")]
    public static string error_string(uint error)
    {
        if (!OperatingSystem.IsWindows()) return new Win32Exception(unchecked((int)error)).Message;
        var buffer = new StringBuilder(ErrorBufferSize);
        var language = checked((uint)CultureInfo.CurrentUICulture.LCID);
        if (FormatMessage(FormatMessageFromSystem | FormatMessageIgnoreInserts, IntPtr.Zero, error, language, buffer, ErrorBufferSize, IntPtr.Zero) == 0 &&
            FormatMessage(FormatMessageFromSystem | FormatMessageIgnoreInserts, IntPtr.Zero, error, 0, buffer, ErrorBufferSize, IntPtr.Zero) == 0)
        {
            return $"system error {error}";
        }
        return buffer.ToString();
    }

    [NssmUpstreamFunction("src/event.cpp", 27, "TCHAR *message_string(unsigned long error)", "NssmEventTests.message_string_reads_compiled_mc_semantics")]
    public static string message_string(uint error) =>
        Catalog.Value.TryGet(error, CultureInfo.CurrentUICulture, out var message)
            ? message
            : $"system error {error}";

    public static uint message_id(string symbolicName) => Catalog.Value.Id(symbolicName);

    [NssmUpstreamFunction("src/event.cpp", 39, "void log_event(unsigned short type, unsigned long id, ...)", "NssmEventTests.log_event_accepts_up_to_fifteen_insertions")]
    public static void log_event(ushort type, uint id, params string?[] insertions)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = RegisterEventSource(null, EventSource);
        if (handle == IntPtr.Zero) return;
        try
        {
            var strings = insertions.Where(value => value is not null).Take(15).Select(value => value!).ToArray();
            ReportEvent(handle, type, 0, id, IntPtr.Zero, checked((ushort)strings.Length), 0, strings, IntPtr.Zero);
        }
        finally
        {
            DeregisterEventSource(handle);
        }
    }

    [NssmUpstreamFunction("src/event.cpp", 62, "void print_message(FILE *file, unsigned long id, ...)", "NssmEventTests.print_message_applies_printf_placeholders")]
    public static void print_message(TextWriter file, uint id, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(file);
        var message = NssmPrintf.Format(message_string(id), arguments).Replace("\n", "\r\n", StringComparison.Ordinal);
        if (!NssmUtf8.IsSetup)
        {
            var characters = message.ToCharArray();
            for (var index = 0; index < characters.Length; index++) if (characters[index] > 0x7f) characters[index] = '?';
            message = new string(characters);
        }
        file.Write(message);
    }

    [NssmUpstreamFunction("src/event.cpp", 76, "int popup_message(HWND owner, unsigned int type, unsigned long id, ...)", "NssmEventTests.popup_message_formats_without_display_in_test_mode")]
    public static int popup_message(IntPtr owner, uint type, uint id, params object?[] arguments)
    {
        var text = NssmPrintf.Format(message_string(id), arguments);
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NSSM_MANAGER_TEST_NO_UI") == "1") return 0;
        return MessageBox(owner, text, "NSSM", type == MbOk ? type : type | MbIconExclamation);
    }

    internal static string FormatForTest(string format, params object?[] arguments) => NssmPrintf.Format(format, arguments);

    private sealed class MessageCatalog
    {
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, Dictionary<string, string>> _messages = [];

        public uint Id(string symbolicName) => _ids.TryGetValue(symbolicName, out var id)
            ? id
            : throw new KeyNotFoundException($"Unknown NSSM message '{symbolicName}'.");

        public bool TryGet(uint id, CultureInfo culture, out string message)
        {
            message = string.Empty;
            if (!_messages.TryGetValue(id, out var languages)) return false;
            var language = culture.TwoLetterISOLanguageName switch
            {
                "fr" => "French",
                "it" => "Italian",
                _ => "English"
            };
            return languages.TryGetValue(language, out message!) || languages.TryGetValue("English", out message!);
        }

        public static MessageCatalog Load()
        {
            var assembly = typeof(NssmEvent).Assembly;
            using var stream = assembly.GetManifestResourceStream("NssmManager.Windows.messages.mc")
                ?? throw new InvalidDataException("Embedded NSSM messages.mc is missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);

            var catalog = new MessageCatalog();
            uint code = 0;
            uint severity = 0;
            string? symbol = null;
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (line.StartsWith("MessageId", StringComparison.Ordinal))
                {
                    var value = line[(line.IndexOf('=') + 1)..].Trim();
                    code = value == "+1" ? code + 1 : uint.Parse(value, CultureInfo.InvariantCulture);
                    severity = 0;
                    symbol = null;
                    continue;
                }
                if (line.StartsWith("SymbolicName", StringComparison.Ordinal))
                {
                    symbol = line[(line.IndexOf('=') + 1)..].Trim();
                    continue;
                }
                if (line.StartsWith("Severity", StringComparison.Ordinal))
                {
                    severity = line[(line.IndexOf('=') + 1)..].Trim() switch
                    {
                        "Informational" => 1u,
                        "Warning" => 2u,
                        "Error" => 3u,
                        _ => 0u
                    };
                    continue;
                }
                if (!line.StartsWith("Language =", StringComparison.Ordinal)) continue;

                var language = line[(line.IndexOf('=') + 1)..].Trim();
                var text = new StringBuilder();
                while (++index < lines.Count && lines[index] != ".")
                {
                    if (text.Length != 0) text.Append("\r\n");
                    text.Append(lines[index]);
                }
                text.Append("\r\n");

                var id = code | (severity << 30);
                if (symbol is not null) catalog._ids[symbol] = id;
                if (!catalog._messages.TryGetValue(id, out var translations))
                {
                    translations = new Dictionary<string, string>(StringComparer.Ordinal);
                    catalog._messages[id] = translations;
                }
                translations[language] = text.ToString();
            }
            return catalog;
        }
    }

    private static class NssmPrintf
    {
        public static string Format(string format, IReadOnlyList<object?> arguments)
        {
            var output = new StringBuilder(format.Length + 64);
            var argument = 0;
            for (var index = 0; index < format.Length; index++)
            {
                if (format[index] != '%' || index + 1 >= format.Length)
                {
                    output.Append(format[index]);
                    continue;
                }
                if (format[index + 1] == '%')
                {
                    output.Append('%');
                    index++;
                    continue;
                }

                var specifierStart = index++;
                while (index < format.Length && "-+ #0".Contains(format[index], StringComparison.Ordinal)) index++;
                var widthStart = index;
                while (index < format.Length && char.IsDigit(format[index])) index++;
                var widthText = format[widthStart..index];
                if (index < format.Length && format[index] == 'I' && index + 2 < format.Length && format.AsSpan(index, 3).SequenceEqual("I64"))
                {
                    index += 3;
                }
                else
                {
                    while (index < format.Length && format[index] is 'h' or 'l' or 'L' or 'z' or 't') index++;
                }
                if (index >= format.Length)
                {
                    output.Append(format.AsSpan(specifierStart));
                    break;
                }

                var specifier = format[index];
                if (argument >= arguments.Count)
                {
                    output.Append(format.AsSpan(specifierStart, index - specifierStart + 1));
                    continue;
                }
                var value = arguments[argument++];
                var rendered = specifier switch
                {
                    's' or 'S' => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
                    'd' or 'i' => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                    'u' => Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                    'x' => Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString("x", CultureInfo.InvariantCulture),
                    'X' => Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString("X", CultureInfo.InvariantCulture),
                    'c' => Convert.ToChar(value, CultureInfo.InvariantCulture).ToString(),
                    _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
                };
                if (int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && rendered.Length < width)
                {
                    var pad = format.AsSpan(specifierStart, index - specifierStart + 1).Contains('0') ? '0' : ' ';
                    rendered = rendered.PadLeft(width, pad);
                }
                output.Append(rendered);
            }
            return output.ToString();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "FormatMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FormatMessage(uint flags, IntPtr source, uint messageId, uint languageId, StringBuilder buffer, int size, IntPtr arguments);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterEventSource(string? serverName, string sourceName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterEventSource(IntPtr eventLog);

    [DllImport("advapi32.dll", EntryPoint = "ReportEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReportEvent(
        IntPtr eventLog,
        ushort type,
        ushort category,
        uint eventId,
        IntPtr userSid,
        ushort numStrings,
        uint dataSize,
        [In] string[] strings,
        IntPtr rawData);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
