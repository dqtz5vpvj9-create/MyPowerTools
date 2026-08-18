namespace InputMonitor.Core;

/// <summary>
/// Maps process image / bundle identifiers onto product categories.
/// Built-in rules use lowercase substring matching; user overrides win.
/// </summary>
public sealed class AppCategoryMap
{
    public static readonly IReadOnlyList<(string[] Keywords, AppCategory Category)> BuiltinRules =
    [
        (["xcode", "vscode", "jetbrains", "idea", "devenv", "visualstudio", "rider", "terminal", "iterm",
            "windows terminal", "wt.exe", "sublime", "emacs", "vim", "neovim", "android.studio", "cursor",
            "trae", "codebuddy", "notepad++", "postman", "docker", "zed", "fleet", "gitkraken", "dash"],
            AppCategory.Development),
        (["safari", "chrome", "firefox", "msedge", "edge", "arc", "brave", "opera", "vivaldi", "orion",
            "chromium", "qqbrowser", "sogou", "iexplore"],
            AppCategory.Browser),
        (["pages", "numbers", "keynote", "winword", "excel", "powerpnt", "office", "wps", "notion",
            "obsidian", "typora", "feishu", "lark", "dingtalk", "youdao", "outlook", "olk.exe", "mail"],
            AppCategory.Office),
        (["photoshop", "figma", "sketch", "illustrator", "affinity", "blender", "canva", "pixelmator",
            "premiere", "aftereffects", "davinci"],
            AppCategory.Design),
        (["wechat", "weixin", "qq", "telegram", "discord", "slack", "whatsapp", "wxwork", "wecom",
            "xiaohongshu", "weibo"],
            AppCategory.Social),
        (["spotify", "neteasemusic", "qqmusic", "vlc", "bilibili", "youtube", "potplayer", "cloudmusic",
            "douyin", "tiktok"],
            AppCategory.Media)
    ];

    private readonly object _gate = new();
    private Dictionary<string, AppCategory> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyDictionary<string, AppCategory> Overrides
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, AppCategory>(_overrides, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void ReplaceOverrides(IReadOnlyDictionary<string, AppCategory> overrides)
    {
        lock (_gate)
        {
            _overrides = new Dictionary<string, AppCategory>(overrides, StringComparer.OrdinalIgnoreCase);
        }

        Changed?.Invoke();
    }

    public AppCategory CategoryFor(string bundleId)
    {
        var id = bundleId.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (_overrides.TryGetValue(id, out var overrideCategory))
            {
                return overrideCategory;
            }
        }

        foreach (var rule in BuiltinRules)
        {
            if (rule.Keywords.Any(keyword => id.Contains(keyword, StringComparison.Ordinal)))
            {
                return rule.Category;
            }
        }

        return AppCategory.Other;
    }

    public void SetOverride(string bundleId, AppCategory? category)
    {
        var id = bundleId.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (category is null)
            {
                _overrides.Remove(id);
            }
            else
            {
                _overrides[id] = category.Value;
            }
        }

        Changed?.Invoke();
    }
}
