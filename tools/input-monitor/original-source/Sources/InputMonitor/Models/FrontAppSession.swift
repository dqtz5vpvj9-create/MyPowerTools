import Foundation

/// 应用类型（分应用类型统计用）
enum AppCategory: String, CaseIterable, Codable {
    case development
    case browser
    case office
    case design
    case social
    case media
    case other

    var displayName: String {
        switch self {
        case .development: return "开发"
        case .browser:     return "浏览器"
        case .office:      return "办公"
        case .design:      return "设计"
        case .social:      return "社交"
        case .media:       return "影音"
        case .other:       return "其他"
        }
    }
}

/// 前台应用窗口活动会话
struct FrontAppSession {
    let bundleID: String
    let appName: String
    var windowTitle: String?   // 辅助功能授权时可用；日志不落明文
    let start: Date
    var end: Date?
    var category: AppCategory

    var duration: TimeInterval? {
        end.map { $0.timeIntervalSince(start) }
    }
}
