import Foundation

/// 疲劳值状态机（严格对应规则）：
/// - 勾选来源的活动时间累计疲劳值：连续活动每 remindIntervalMinutes 分钟 = 100 点
/// - 超过 2 分钟无任何活动则暂停累计（离开电脑不累疲劳）
/// - 值 ≥ 100 触发提醒；跳过 → 值=100、阈值=120 继续累计，再达 120 再提醒（循环）
/// - 休息倒计时完成 → 值=0、阈值恢复 100
/// - 手动休息：以当前值直接进入提醒
/// - 暂停提醒：不弹窗，但活动与疲劳值照常累计
final class FatigueEngine: ObservableObject {
    enum ActivitySource {
        case keyboard, mouse, app
    }

    @Published private(set) var value: Double = 0
    @Published private(set) var threshold: Double = 100
    @Published private(set) var isResting = false
    /// 暂停提醒（继续记录与累计）
    @Published private(set) var isPaused = false

    /// 达到阈值触发提醒（主线程）
    var onShouldRemind: (() -> Void)?

    private let settings: SettingsStore
    private let defaults = UserDefaults.standard
    private var timer: Timer?
    private var lastActivityDate: Date?

    /// 无活动暂停累计的空闲阈值
    private let idleGap: TimeInterval = 120

    private enum PersistKeys {
        static let value = "fatigueValue"
        static let threshold = "fatigueThreshold"
        static let isPaused = "fatigueIsPaused"
    }

    var percentage: Int { Int(value.rounded()) }

    init(settings: SettingsStore = .shared) {
        self.settings = settings
        value = defaults.object(forKey: PersistKeys.value) as? Double ?? 0
        threshold = defaults.object(forKey: PersistKeys.threshold) as? Double ?? 100
        isPaused = defaults.object(forKey: PersistKeys.isPaused) as? Bool ?? false
    }

    func start() {
        stop()
        let timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            self?.tick()
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    /// 报告一次活动（事件流/窗口心跳调用；频率高，内部仅更新时间戳）
    func notifyActivity(source: ActivitySource) {
        switch source {
        case .keyboard: guard settings.fatigueFromKeyboard else { return }
        case .mouse:    guard settings.fatigueFromMouse else { return }
        case .app:      guard settings.fatigueFromApp else { return }
        }
        lastActivityDate = Date()
    }

    /// 手动休息前备份（取消手动休息时恢复原值）
    private var manualRestBackup: (value: Double, threshold: Double)?

    /// 手动休息：备份当前值并进入提醒
    /// - 休息完成 → 值置 0；取消（跳过）→ 恢复原值
    func manualRest() {
        guard !isResting else { return }
        manualRestBackup = (value, threshold)
        DispatchQueue.main.async { self.onShouldRemind?() }
    }

    /// 提醒窗口已展示（由 RestReminderController 回调）
    func beginResting() {
        isResting = true
    }

    /// 跳过本次：手动休息取消 → 恢复原值；自动提醒跳过 → 值置 100、阈值升至 120 继续累计
    func skip() {
        if let backup = manualRestBackup {
            value = backup.value
            threshold = backup.threshold
            manualRestBackup = nil
        } else {
            value = 100
            threshold = 120
        }
        isResting = false
        persist()
    }

    /// 休息完成：值清零、阈值恢复 100
    func restDone() {
        value = 0
        threshold = 100
        manualRestBackup = nil
        isResting = false
        persist()
    }

    /// 切换暂停提醒（继续记录活动与累计）
    func setPaused(_ paused: Bool) {
        isPaused = paused
        defaults.set(paused, forKey: PersistKeys.isPaused)
        // 恢复时若已超阈值且配置了立即提醒 → 提醒一次
        if !paused, settings.remindAfterResume, value >= threshold {
            DispatchQueue.main.async { self.onShouldRemind?() }
        }
    }

    // MARK: - 私有

    private func tick() {
        guard !isResting else { return }
        guard let last = lastActivityDate, Date().timeIntervalSince(last) <= idleGap else { return }
        let pointsPerSecond = 100.0 / max(1, settings.remindIntervalMinutes * 60)
        value += pointsPerSecond

        if value >= threshold, !isPaused {
            persist()
            onShouldRemind?()
        } else if Int(value) % 10 == 0 {
            // 周期性落盘（每涨 10 点左右）
            persist()
        }
    }

    private func persist() {
        defaults.set(value, forKey: PersistKeys.value)
        defaults.set(threshold, forKey: PersistKeys.threshold)
    }
}
