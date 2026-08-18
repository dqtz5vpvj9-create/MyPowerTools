import Foundation

/// bundleID → 应用类型映射（内置规则 + 用户自定义覆盖，覆盖持久化到 UserDefaults）
final class AppCategoryMap: ObservableObject {
    static let shared = AppCategoryMap()

    /// 覆盖配置变更通知（看板据此实时刷新统计）
    static let didChangeNotification = Notification.Name("AppCategoryMapDidChange")

    private let defaultsKey = "appCategoryOverrides"
    /// @Published：设置页 SwiftUI 视图据此即时刷新
    @Published private(set) var overrides: [String: AppCategory] = [:]

    /// 内置规则：bundleID 小写包含关键词即命中（先命中的规则优先）
    static let builtinRules: [(keywords: [String], category: AppCategory)] = [
        (["xcode", "vscode", "jetbrains", "idea", "terminal", "iterm", "sublime", "emacs", "vim", "neovim", "android.studio", "cursor", "trae", "codebuddy", "tower", "sourcetree", "fork", "postman", "docker", "forklift", "nova", "zed", "fleet", "gitkraken", "dash"], .development),
        (["safari", "chrome", "firefox", "edge", "arc", "brave", "opera", "vivaldi", "orion", "chromium", "qqbrowser", "sogou"], .browser),
        (["pages", "numbers", "keynote", "word", "excel", "powerpoint", "office", "wps", "notion", "obsidian", "typora", "feishu", "lark", "dingtalk", "youdao.note", "bear", "ulysses", "craft", "mail", "outlook", "calendar", "reminders"], .office),
        (["photoshop", "figma", "sketch", "illustrator", "affinity", "blender", "canva", "pixelmator", "cinema4d", "finalcut", "premiere", "aftereffects", "davinci"], .design),
        (["wechat", "qq", "telegram", "discord", "slack", "whatsapp", "messages", "imessage", "weibo", "xiaohongshu", "momo", "soul"], .social),
        (["music", "spotify", "neteasemusic", "qqmusic", "vlc", "iina", "bilibili", "youtube", "tv", "quicktime", "infuse", "plex", "douyin", "tiktok"], .media),
    ]

    private init() {
        loadOverrides()
    }

    func category(for bundleID: String) -> AppCategory {
        let id = bundleID.lowercased()
        if let override = overrides[id] { return override }
        for rule in Self.builtinRules {
            if rule.keywords.contains(where: { id.contains($0) }) {
                return rule.category
            }
        }
        return .other
    }

    func setOverride(_ category: AppCategory?, for bundleID: String) {
        let id = bundleID.lowercased()
        overrides[id] = category
        saveOverrides()
        NotificationCenter.default.post(name: Self.didChangeNotification, object: nil)
    }

    private func loadOverrides() {
        guard let raw = UserDefaults.standard.dictionary(forKey: defaultsKey) as? [String: String] else { return }
        overrides = raw.compactMapValues { AppCategory(rawValue: $0) }
    }

    private func saveOverrides() {
        UserDefaults.standard.set(overrides.mapValues { $0.rawValue }, forKey: defaultsKey)
    }
}
