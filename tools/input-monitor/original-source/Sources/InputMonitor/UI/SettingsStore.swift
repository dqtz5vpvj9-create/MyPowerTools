import Foundation
import ServiceManagement

/// 配置中心：全部能力可在设置页配置，UserDefaults 持久化
final class SettingsStore: ObservableObject {
    static let shared = SettingsStore()

    private let defaults = UserDefaults.standard

    private enum Keys {
        static let remindIntervalMinutes = "remindIntervalMinutes"
        static let restDurationSeconds = "restDurationSeconds"
        static let legacyRestDurationMinutes = "restDurationMinutes"
        static let fatigueFromKeyboard = "fatigueFromKeyboard"
        static let fatigueFromMouse = "fatigueFromMouse"
        static let fatigueFromApp = "fatigueFromApp"
        static let soundEnabled = "soundEnabled"
        static let soundVolume = "soundVolume"
        static let soundName = "soundName"
        static let privacyMode = "privacyMode"
        static let trackSampleDistance = "trackSampleDistance"
        static let appHeartbeatSeconds = "appHeartbeatSeconds"
        static let remindAfterResume = "remindAfterResume"
        static let launchAtLogin = "launchAtLogin"
        static let dataRetentionDays = "dataRetentionDays"
    }

    /// 提醒间隔：连续活动多少分钟 = 疲劳值 100（5–120）
    @Published var remindIntervalMinutes: Double {
        didSet { defaults.set(remindIntervalMinutes, forKey: Keys.remindIntervalMinutes) }
    }
    /// 休息倒计时时长（秒，10–7200，键盘输入精确到秒）
    @Published var restDurationSeconds: Int {
        didSet {
            let clamped = min(max(restDurationSeconds, 10), 7200)
            if clamped != restDurationSeconds {
                restDurationSeconds = clamped // 观察者内赋值不会递归触发
            }
            defaults.set(clamped, forKey: Keys.restDurationSeconds)
        }
    }
    /// 疲劳计入来源开关
    @Published var fatigueFromKeyboard: Bool {
        didSet { defaults.set(fatigueFromKeyboard, forKey: Keys.fatigueFromKeyboard) }
    }
    @Published var fatigueFromMouse: Bool {
        didSet { defaults.set(fatigueFromMouse, forKey: Keys.fatigueFromMouse) }
    }
    @Published var fatigueFromApp: Bool {
        didSet { defaults.set(fatigueFromApp, forKey: Keys.fatigueFromApp) }
    }
    /// 休息结束提示音
    @Published var soundEnabled: Bool {
        didSet { defaults.set(soundEnabled, forKey: Keys.soundEnabled) }
    }
    @Published var soundVolume: Double {
        didSet { defaults.set(soundVolume, forKey: Keys.soundVolume) }
    }
    @Published var soundName: String {
        didSet { defaults.set(soundName, forKey: Keys.soundName) }
    }
    /// 隐私模式：只记 keyCode 次数，不落按键字符
    @Published var privacyMode: Bool {
        didSet { defaults.set(privacyMode, forKey: Keys.privacyMode) }
    }
    /// 轨迹采样最小距离（px）
    @Published var trackSampleDistance: Double {
        didSet { defaults.set(trackSampleDistance, forKey: Keys.trackSampleDistance) }
    }
    /// 前台窗口心跳间隔（秒）
    @Published var appHeartbeatSeconds: Double {
        didSet { defaults.set(appHeartbeatSeconds, forKey: Keys.appHeartbeatSeconds) }
    }
    /// 暂停提醒恢复后：若已超阈值是否立即提醒
    @Published var remindAfterResume: Bool {
        didSet { defaults.set(remindAfterResume, forKey: Keys.remindAfterResume) }
    }
    /// 开机自动启动（SMAppService 注册登录项，系统状态为准，失败回滚）
    @Published var launchAtLogin: Bool {
        didSet {
            do {
                if launchAtLogin {
                    try SMAppService.mainApp.register()
                } else {
                    try SMAppService.mainApp.unregister()
                }
                defaults.set(launchAtLogin, forKey: Keys.launchAtLogin)
            } catch {
                FileLogger.log("launchAtLogin toggle FAILED: \(error.localizedDescription)")
                launchAtLogin = oldValue // 观察者内赋值不会递归触发
            }
        }
    }
    /// 数据保存周期（天，默认 365 = 1 年；超期数据启动时自动清理）
    @Published var dataRetentionDays: Int {
        didSet {
            let clamped = min(max(dataRetentionDays, 1), 36500)
            if clamped != dataRetentionDays {
                dataRetentionDays = clamped // 观察者内赋值不会递归触发
            }
            defaults.set(clamped, forKey: Keys.dataRetentionDays)
        }
    }

    private init() {
        let d = UserDefaults.standard
        remindIntervalMinutes = d.object(forKey: Keys.remindIntervalMinutes) as? Double ?? 20
        // 旧版分钟配置一次性迁移到秒
        let migratedRestSeconds: Int
        if let seconds = d.object(forKey: Keys.restDurationSeconds) as? Int {
            migratedRestSeconds = seconds
        } else if let legacyMinutes = d.object(forKey: Keys.legacyRestDurationMinutes) as? Double {
            migratedRestSeconds = min(max(Int(legacyMinutes * 60), 10), 7200)
            d.set(migratedRestSeconds, forKey: Keys.restDurationSeconds)
            d.removeObject(forKey: Keys.legacyRestDurationMinutes)
        } else {
            migratedRestSeconds = 300
        }
        restDurationSeconds = migratedRestSeconds
        fatigueFromKeyboard = d.object(forKey: Keys.fatigueFromKeyboard) as? Bool ?? true
        fatigueFromMouse = d.object(forKey: Keys.fatigueFromMouse) as? Bool ?? true
        fatigueFromApp = d.object(forKey: Keys.fatigueFromApp) as? Bool ?? true
        soundEnabled = d.object(forKey: Keys.soundEnabled) as? Bool ?? true
        soundVolume = d.object(forKey: Keys.soundVolume) as? Double ?? 0.8
        soundName = d.object(forKey: Keys.soundName) as? String ?? "Glass"
        privacyMode = d.object(forKey: Keys.privacyMode) as? Bool ?? false
        trackSampleDistance = d.object(forKey: Keys.trackSampleDistance) as? Double ?? 30
        appHeartbeatSeconds = d.object(forKey: Keys.appHeartbeatSeconds) as? Double ?? 30
        remindAfterResume = d.object(forKey: Keys.remindAfterResume) as? Bool ?? true
        // 以系统登录项实际状态为准（用户可能在系统设置里改动过）
        launchAtLogin = SMAppService.mainApp.status == .enabled
        dataRetentionDays = d.object(forKey: Keys.dataRetentionDays) as? Int ?? 365
    }
}
