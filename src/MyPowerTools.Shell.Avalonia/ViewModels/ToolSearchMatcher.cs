using System.Text;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

/// <summary>The same Unicode/word matching and relevance order for catalog and command-palette searches.</summary>
public static class ToolSearchMatcher
{
    /// <returns>A relevance score, or -1 when any query word is missing.</returns>
    public static int Score(string? query, string title, string id, params string[] metadata)
    {
        var phrase = Normalize(query);
        if (phrase.Length == 0) return 0;
        var name = Normalize(title);
        var identifier = Normalize(id);
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = string.Join(' ', new[] { name, identifier }.Concat(metadata.Select(Normalize)));
        if (!words.All(word => haystack.Contains(word, StringComparison.Ordinal))) return -1;
        if (name == phrase || identifier == phrase) return 100;
        if (name.StartsWith(phrase, StringComparison.Ordinal) || identifier.StartsWith(phrase, StringComparison.Ordinal)) return 80;
        if (words.All(word => name.Contains(word, StringComparison.Ordinal) || identifier.Contains(word, StringComparison.Ordinal))) return 60;
        return 20;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character) || character is '-' or '_' or '/' or '.')
            {
                if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString().Trim();
    }
}
