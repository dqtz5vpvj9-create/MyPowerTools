import CoreGraphics
import Foundation

/// 屏幕状态：锁屏期间窗口活动心跳不计入互动时长
enum ScreenState {
    static var isLocked: Bool {
        guard let dict = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
        return (dict["CGSSessionScreenIsLocked"] as? Bool) ?? false
    }
}
