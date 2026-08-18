import AppKit
import ApplicationServices
import Foundation

/// 前台应用/窗口活动追踪：
/// - NSWorkspace.didActivateApplicationNotification 感知 App 切换
/// - 周期心跳感知同 App 内窗口标题变化（如浏览器换标签）
/// - 生成 FrontAppSession（App/窗口/起止/类型），供落库与统计
final class FrontAppTracker {
    /// 会话结束（切换/退出）时回调
    var onSession: ((FrontAppSession) -> Void)?
    /// 心跳回调（窗口活动强度信号，供疲劳引擎与活动时长统计）
    var onHeartbeat: ((FrontAppSession, Date) -> Void)?

    var heartbeatInterval: TimeInterval

    private let categoryMap: AppCategoryMap
    private var current: FrontAppSession?
    private var heartbeatTimer: Timer?
    private let lock = NSLock()
    /// 锁屏/熄屏期间挂起：不计窗口活动时间
    private var isSuspended = false

    init(categoryMap: AppCategoryMap = .shared, heartbeatInterval: TimeInterval = 30) {
        self.categoryMap = categoryMap
        self.heartbeatInterval = heartbeatInterval
    }

    func start() {
        NSWorkspace.shared.notificationCenter.addObserver(
            self,
            selector: #selector(appActivated(_:)),
            name: NSWorkspace.didActivateApplicationNotification,
            object: nil
        )
        // 锁屏/熄屏：结束会话并挂起；解锁/亮屏：恢复追踪
        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(self, selector: #selector(onScreenLocked), name: .init("com.apple.screenIsLocked"), object: nil)
        dnc.addObserver(self, selector: #selector(onScreenUnlocked), name: .init("com.apple.screenIsUnlocked"), object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(onScreenLocked), name: NSWorkspace.screensDidSleepNotification, object: nil
        )
        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(onScreenUnlocked), name: NSWorkspace.screensDidWakeNotification, object: nil
        )
        beginSessionFromFrontmost()
        let timer = Timer.scheduledTimer(withTimeInterval: heartbeatInterval, repeats: true) { [weak self] _ in
            self?.heartbeat()
        }
        RunLoop.main.add(timer, forMode: .common)
        heartbeatTimer = timer
        FileLogger.log("front app tracker started")
    }

    func stop() {
        heartbeatTimer?.invalidate()
        heartbeatTimer = nil
        NSWorkspace.shared.notificationCenter.removeObserver(self)
        DistributedNotificationCenter.default().removeObserver(self)
        lock.lock()
        endCurrentLocked(at: Date())
        lock.unlock()
    }

    /// 热更新心跳间隔（设置页调整立即生效；仅在运行中重建定时器）
    func updateHeartbeatInterval(_ interval: TimeInterval) {
        heartbeatInterval = interval
        DispatchQueue.main.async {
            guard self.heartbeatTimer != nil else { return }
            self.heartbeatTimer?.invalidate()
            let timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
                self?.heartbeat()
            }
            RunLoop.main.add(timer, forMode: .common)
            self.heartbeatTimer = timer
            FileLogger.log("heartbeat interval updated → \(Int(interval))s")
        }
    }

    /// 锁屏/熄屏：以当前时刻结束会话，锁屏时长不计入窗口活动
    @objc private func onScreenLocked() {
        lock.lock()
        isSuspended = true
        endCurrentLocked(at: Date())
        lock.unlock()
        FileLogger.log("screen locked → session ended, tracker suspended")
    }

    /// 解锁/亮屏：恢复追踪
    @objc private func onScreenUnlocked() {
        lock.lock()
        isSuspended = false
        lock.unlock()
        beginSessionFromFrontmost()
        FileLogger.log("screen unlocked → tracker resumed")
    }

    /// 当前前台会话快照（供状态栏/疲劳引擎查询）
    func currentSession() -> FrontAppSession? {
        lock.lock()
        defer { lock.unlock() }
        return current
    }

    @objc private func appActivated(_ note: Notification) {
        guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
        switchTo(app: app)
    }

    private func beginSessionFromFrontmost() {
        guard let app = NSWorkspace.shared.frontmostApplication else { return }
        switchTo(app: app)
    }

    private func switchTo(app: NSRunningApplication) {
        lock.lock()
        if isSuspended {
            lock.unlock()
            return
        }
        lock.unlock()
        let now = Date()
        let bundleID = app.bundleIdentifier ?? "unknown.bundle"
        let name = app.localizedName ?? bundleID
        let title = fetchWindowTitle(pid: app.processIdentifier)
        lock.lock()
        endCurrentLocked(at: now)
        current = FrontAppSession(
            bundleID: bundleID,
            appName: name,
            windowTitle: title,
            start: now,
            category: categoryMap.category(for: bundleID)
        )
        lock.unlock()
    }

    private func heartbeat() {
        lock.lock()
        if isSuspended {
            lock.unlock()
            return
        }
        lock.unlock()
        let now = Date()
        guard let app = NSWorkspace.shared.frontmostApplication else { return }
        let title = fetchWindowTitle(pid: app.processIdentifier)
        lock.lock()
        if var session = current {
            if session.windowTitle != title, title != nil {
                // 同 App 内窗口标题变化：结束旧会话、开启新会话
                session.end = now
                let finished = session
                current = FrontAppSession(
                    bundleID: session.bundleID,
                    appName: session.appName,
                    windowTitle: title,
                    start: now,
                    category: session.category
                )
                let handler = onSession
                lock.unlock()
                if (finished.duration ?? 0) >= 1 { handler?(finished) }
                return
            }
            let snapshot = session
            lock.unlock()
            onHeartbeat?(snapshot, now)
        } else {
            lock.unlock()
            switchTo(app: app)
        }
    }

    /// 调用前必须已持有 lock
    private func endCurrentLocked(at date: Date) {
        guard var session = current else { return }
        session.end = date
        current = nil
        if (session.duration ?? 0) >= 1 {
            let handler = onSession
            // 回调可能触库，放锁外执行
            DispatchQueue.global(qos: .utility).async { handler?(session) }
        }
    }

    /// 读取前台 App 聚焦窗口标题（需辅助功能权限；未授权返回 nil）
    private func fetchWindowTitle(pid: pid_t) -> String? {
        let appRef = AXUIElementCreateApplication(pid)
        var windowValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(appRef, kAXFocusedWindowAttribute as CFString, &windowValue) == .success,
              let window = windowValue else {
            return nil
        }
        var titleValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(window as! AXUIElement, kAXTitleAttribute as CFString, &titleValue) == .success else {
            return nil
        }
        return titleValue as? String
    }
}
