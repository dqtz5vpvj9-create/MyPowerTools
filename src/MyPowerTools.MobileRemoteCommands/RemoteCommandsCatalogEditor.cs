using System.Globalization;
using System.Text;
using RemoteCommands.Surface.Services;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// The values the phone edits for one <c>commands.yaml</c> entry.
/// </summary>
internal sealed record MobileCommandDraft(
    string Id,
    string Label,
    string Command,
    string Description,
    string Type,
    string Host,
    string Input1Label,
    string Input1Placeholder,
    string Input2Label,
    string Input2Placeholder,
    bool ShowSecondInput)
{
    public static MobileCommandDraft FromDefinition(MobileCommandDefinition definition) => new(
        definition.Id,
        definition.Label,
        definition.Command,
        definition.Description,
        definition.IsLocalTransform ? "py" : "shell",
        definition.Host,
        definition.Input1Label,
        definition.Input1Placeholder,
        definition.Input2Label,
        definition.Input2Placeholder,
        definition.ShowSecondInput);

    public bool UsesRemoteHost => !string.Equals(Type, "py", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Text transforms for the shared <c>commands.yaml</c>.
/// </summary>
/// <remarks>
/// The phone never writes the file itself: every transform here produces document text that is handed
/// to <c>remote-commands-android.catalog.save</c>, which validates it with the shipped parser
/// (<see cref="RemoteCommandsYaml"/>) and only then writes the canonical file. These helpers keep the
/// untouched part of the document byte-for-byte, so editing one command on the phone cannot drop the
/// comments, ordering or extra keys of the others.
///
/// The format is the flat one the shipped parser reads: a top-level <c>commands:</c> key with
/// <c>- id: …</c> entries and <c>key: value</c> continuation lines. Values are single-line and are
/// written double-quoted; the parser strips one layer of quotes, so a value containing a quote
/// round-trips unchanged.
/// </remarks>
internal static class MobileCommandsYamlEditor
{
    private const string EntryPrefix = "- ";

    private static readonly string[] DefaultPlaceholders =
    [
        "Paste or type the command input.",
        "Optional second input."
    ];

    /// <summary>Command ids are also deep-link keys and history keys, so they keep a slug shape.</summary>
    public static bool IsValidIdentifier(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmed = id.Trim();
        if (!char.IsLetterOrDigit(trimmed[0]))
        {
            return false;
        }

        return trimmed.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    /// <summary>
    /// Derives a stable id from the label (ASCII) with a hash fallback, so a Chinese label still gets a
    /// deterministic, unique-looking id and the user can always override it.
    /// </summary>
    public static string SuggestId(string label, string command)
    {
        var builder = new StringBuilder();
        foreach (var character in (label ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (character is ' ' or '-' or '_' or '.' && builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var slug = builder.ToString().Trim('_', '.', '-');
        if (slug.Length > 0 && char.IsAsciiLetterOrDigit(slug[0]))
        {
            return slug.Length > 48 ? slug[..48] : slug;
        }

        return "cmd_" + StableHash((label ?? "") + "\u0000" + (command ?? "")).ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>Adds one entry to the current document, preserving every other line.</summary>
    public static bool TryAdd(string yaml, MobileCommandDraft draft, out string updated, out string error) =>
        TryWrite(yaml, draft, originalId: null, remove: false, out updated, out error);

    /// <summary>Replaces the entry whose id is <paramref name="originalId"/>; the id itself may change.</summary>
    public static bool TryUpdate(string yaml, string originalId, MobileCommandDraft draft, out string updated, out string error) =>
        TryWrite(yaml, draft, originalId, remove: false, out updated, out error);

    /// <summary>Removes one entry, refusing to leave a document the shipped validator would reject.</summary>
    public static bool TryRemove(string yaml, string id, out string updated, out string error) =>
        TryWrite(yaml, draft: null, id, remove: true, out updated, out error);

    /// <summary>Serializes a complete document for a catalog that has no <c>commands:</c> section yet.</summary>
    public static string SerializeNewDocument(IEnumerable<MobileCommandDraft> drafts)
    {
        var builder = new StringBuilder();
        builder.Append("commands:").Append('\n');
        foreach (var draft in drafts)
        {
            AppendEntry(builder, draft);
        }

        return builder.ToString();
    }

    /// <summary>Creates the draft that the "添加命令" sheet starts from.</summary>
    public static MobileCommandDraft NewDraft(string label = "", string command = "") => new(
        Id: "",
        Label: label,
        Command: command,
        Description: "",
        Type: "shell",
        Host: "",
        Input1Label: "输入内容",
        Input1Placeholder: DefaultPlaceholders[0],
        Input2Label: "附加输入",
        Input2Placeholder: DefaultPlaceholders[1],
        ShowSecondInput: false);

    private static bool TryWrite(
        string yaml,
        MobileCommandDraft? draft,
        string? originalId,
        bool remove,
        out string updated,
        out string error)
    {
        updated = yaml;
        error = "";

        if (draft is not null && !ValidateDraft(draft, out error))
        {
            return false;
        }

        var text = yaml ?? "";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var section = FindCommandsSection(lines);
        var entries = section is null ? [] : FindEntries(lines, section.Value);

        // A document without a commands: section can only be extended when it is empty: rebuilding a
        // file that already holds text would silently discard whatever the user wrote there.
        if (section is null)
        {
            if (text.Trim().Length > 0 || remove)
            {
                error = remove
                    ? "当前 commands.yaml 里找不到要删除的命令。"
                    : "当前 commands.yaml 缺少顶层 commands: 段，请先在“命令配置”里修正，或清空文件后再添加。";
                return false;
            }

            updated = SerializeNewDocument([draft!]);
            return true;
        }

        if (remove)
        {
            var targetIndex = FindEntryIndex(lines, entries, originalId!);
            if (targetIndex < 0)
            {
                error = $"commands.yaml 里没有 id 为 '{originalId}' 的命令。";
                return false;
            }

            var target = entries[targetIndex];
            lines.RemoveRange(target.Start, target.End - target.Start);
            var remaining = FindEntries(lines, FindCommandsSection(lines)!.Value);
            if (remaining.Count == 0)
            {
                error = "至少要保留一条命令：commands.yaml 不允许空的 commands 列表，请先把新命令加进去。";
                return false;
            }

            updated = string.Join(newline, lines);
            return true;
        }

        var duplicate = entries
            .Select(entry => ReadId(lines, entry))
            .FirstOrDefault(id => string.Equals(id, draft!.Id, StringComparison.OrdinalIgnoreCase) &&
                                  !string.Equals(id, originalId, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            error = $"命令 id '{duplicate}' 已经存在，请换一个标识。";
            return false;
        }

        var replacement = new List<string>();
        AppendEntryLines(replacement, draft!);

        if (originalId is null)
        {
            // Append after the last entry so the section keeps its existing order and separators.
            var insertAt = entries.Count == 0 ? section.Value.Start + 1 : entries[^1].End;
            var blank = insertAt > 0 && insertAt <= lines.Count && lines[insertAt - 1].Trim().Length == 0;
            if (!blank && entries.Count > 0)
            {
                replacement.Insert(0, "");
            }

            lines.InsertRange(insertAt, replacement);
        }
        else
        {
            var targetIndex = FindEntryIndex(lines, entries, originalId);
            if (targetIndex < 0)
            {
                error = $"commands.yaml 里没有 id 为 '{originalId}' 的命令，请重新载入后再编辑。";
                return false;
            }

            var target = entries[targetIndex];
            lines.RemoveRange(target.Start, target.End - target.Start);
            lines.InsertRange(target.Start, replacement);
        }

        updated = string.Join(newline, lines);
        return true;
    }

    private static int FindEntryIndex(List<string> lines, List<(int Start, int End)> entries, string id)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (string.Equals(ReadId(lines, entries[index]), id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ValidateDraft(MobileCommandDraft draft, out string error)
    {
        if (!IsValidIdentifier(draft.Id))
        {
            error = "标识只能包含字母、数字、点、下划线和连字符，并且要以字母或数字开头。";
            return false;
        }

        if (draft.Label.Trim().Length == 0)
        {
            error = "请填写名称。";
            return false;
        }

        if (draft.Command.Trim().Length == 0)
        {
            error = "请填写命令。";
            return false;
        }

        if (!string.Equals(draft.Type, "shell", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(draft.Type, "py", StringComparison.OrdinalIgnoreCase))
        {
            error = "类型只能是 shell（在电脑上执行）或 py（在手机上本地转换）。";
            return false;
        }

        if (draft.Host.Trim().Length > 0 && !RemoteCommandsStore.IsValidHost(draft.Host))
        {
            error = "固定主机只能是一个不含空格的别名。";
            return false;
        }

        // The document format is line oriented: a newline inside a value would silently create a key.
        foreach (var (name, value) in new[]
                 {
                     ("名称", draft.Label),
                     ("命令", draft.Command),
                     ("说明", draft.Description),
                     ("标识", draft.Id),
                     ("主机", draft.Host),
                     ("输入 1 标签", draft.Input1Label),
                     ("输入 1 提示", draft.Input1Placeholder),
                     ("输入 2 标签", draft.Input2Label),
                     ("输入 2 提示", draft.Input2Placeholder)
                 })
        {
            if (value.Contains('\n') || value.Contains('\r'))
            {
                error = $"{name}不能包含换行。";
                return false;
            }
        }

        error = "";
        return true;
    }

    /// <summary>Finds the line index range of the top-level <c>commands:</c> section.</summary>
    private static (int Start, int End)? FindCommandsSection(List<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var isTopLevelKey = line.Length > 0 && !char.IsWhiteSpace(line[0]) && trimmed.EndsWith(':');
            if (!isTopLevelKey)
            {
                continue;
            }

            if (!string.Equals(trimmed[..^1], "commands", StringComparison.Ordinal))
            {
                continue;
            }

            for (var end = index + 1; end < lines.Count; end++)
            {
                var candidate = lines[end];
                var candidateTrimmed = candidate.Trim();
                if (candidateTrimmed.Length == 0 || candidateTrimmed.StartsWith('#'))
                {
                    continue;
                }

                if (candidate.Length > 0 && !char.IsWhiteSpace(candidate[0]) && candidateTrimmed.EndsWith(':'))
                {
                    return (index, end);
                }
            }

            return (index, lines.Count);
        }

        return null;
    }

    private static List<(int Start, int End)> FindEntries(List<string> lines, (int Start, int End) section)
    {
        var entries = new List<(int Start, int End)>();
        var start = -1;
        for (var index = section.Start + 1; index < section.End; index++)
        {
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith(EntryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (start >= 0)
            {
                entries.Add((start, index));
            }

            start = index;
        }

        if (start >= 0)
        {
            entries.Add((start, section.End));
        }

        // Trailing blank/comment lines belong to the section, not to the last entry: trimming them keeps
        // an append from pushing the new entry above them.
        if (entries.Count > 0)
        {
            var last = entries[^1];
            var end = last.End;
            while (end > last.Start + 1 && IsBlankOrComment(lines[end - 1]))
            {
                end--;
            }

            entries[^1] = (last.Start, end);
        }

        return entries;
    }

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private static string ReadId(List<string> lines, (int Start, int End) entry)
    {
        for (var index = entry.Start; index < entry.End; index++)
        {
            var text = lines[index].Trim();
            if (text.StartsWith(EntryPrefix, StringComparison.Ordinal))
            {
                text = text[EntryPrefix.Length..].TrimStart();
            }

            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (!string.Equals(text[..separator].Trim(), "id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = text[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            return value;
        }

        return "";
    }

    private static void AppendEntry(StringBuilder builder, MobileCommandDraft draft)
    {
        var lines = new List<string>();
        AppendEntryLines(lines, draft);
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }
    }

    private static void AppendEntryLines(List<string> lines, MobileCommandDraft draft)
    {
        lines.Add($"  - id: {draft.Id.Trim()}");
        lines.Add($"    label: {Quote(draft.Label)}");
        lines.Add($"    command: {Quote(draft.Command)}");
        lines.Add($"    description: {Quote(draft.Description)}");
        lines.Add($"    type: {Quote(draft.Type)}");
        if (draft.Host.Trim().Length > 0)
        {
            lines.Add($"    host: {Quote(draft.Host)}");
        }

        lines.Add($"    input1_label: {Quote(draft.Input1Label)}");
        lines.Add($"    input1_placeholder: {Quote(draft.Input1Placeholder)}");
        if (draft.ShowSecondInput)
        {
            lines.Add($"    input2_label: {Quote(draft.Input2Label)}");
            lines.Add($"    input2_placeholder: {Quote(draft.Input2Placeholder)}");
            lines.Add("    show_second_input: true");
        }
    }

    /// <summary>
    /// The shipped parser strips one layer of surrounding quotes and keeps the inside verbatim, so the
    /// value is written as-is between quotes; escaping would change what the parser reads back.
    /// </summary>
    private static string Quote(string? value) => "\"" + (value ?? "").Trim() + "\"";

    private static uint StableHash(string value)
    {
        // FNV-1a: stable across runs and platforms, unlike string.GetHashCode.
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return hash;
    }
}
