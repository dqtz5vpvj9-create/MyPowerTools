import AppKit
import Foundation
import SwiftUI

/// 中心协调器：装配权限、采集引擎、聚合管道、存储、疲劳引擎与状态栏
final class AppState: ObservableObject {
    let settings = SettingsStore.shared
    let permissionManager = PermissionManager()
    let eventTapEngine = EventTapEngine()
    lazy var cursorPoller = CursorPoller(queue: eventTapEngine.eventQueue, sampleMinDistance: settings.trackSampleDistance)
    lazy var frontAppTracker = FrontAppTracker(heartbeatInterval: settings.appHeartbeatSeconds)
    let aggregator = MetricsAggregator()
    let buffer = EventBuffer(capacity: 500, flushInterval: 5)
    lazy var fatigue = FatigueEngine(settings: settings)
    lazy var reminder = RestReminderController(fatigue: fatigue, settings: settings)

    private(set) var repository: EventRepository?

    /// 是否已获得采集权限（驱动权限引导 UI）
    @Published private(set) var isCaptureRunning = false
    /// 今日实时计数快照（主线程发布，供状态栏/面板展示）
    @Published private(set) var todayKeyCount = 0
    @Published private(set) var todayClickCount = 0
    @Published private(set) var todayScrollCount = 0
    @Published private(set) var todayMoveDistance = 0.0
    @Published private(set) var todayActiveSeconds = 0.0
    /// 今日互动总秒数（键鼠 ∪ 窗口活动，分钟粒度去重）
    @Published private(set) var todayInteractionSeconds = 0.0

    private var snapshotTimer: Timer?
    private var windowControllers: [NSWindowController] = []
    private var hasStarted = false
    /// 互动时长基线：启动时从 DB 查得的当日去重分钟数（内存分钟桶只覆盖启动后时段）
    private var interactionBaseline: Double = 0
    private var interactionBaselineDay = ""

    // MARK: - 启动

    func start() {
        guard !hasStarted else { return }
        hasStarted = true
        FileLogger.bootstrap()
        FileLogger.log("AppState.start")
        setupStorage()
        setupPipeline()
        setupPermissions()
        fatigue.start()
        startSnapshotTimer()
    }

    private func setupStorage() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dbURL = appSupport
            .appendingPathComponent("InputMonitor", isDirectory: true)
            .appendingPathComponent("input-monitor.db")
        do {
            let db = try Database(url: dbURL)
            repository = EventRepository(db: db)
            FileLogger.log("database opened: \(dbURL.path)")
            // 按保存周期清理过期历史数据（异步，不阻塞启动）
            repository?.purgeExpiredData(retentionDays: settings.dataRetentionDays)
            // 互动时长基线：DB 全天去重分钟数，叠加启动后的实时分钟桶，避免重启后状态栏清零
            if let repo = repository {
                let today = EventRepository.dayString()
                DispatchQueue.global(qos: .userInitiated).async {
                    let baseline = repo.interactionSeconds(day: today)
                    DispatchQueue.main.async {
                        self.interactionBaseline = baseline
                        self.interactionBaselineDay = today
                    }
                }
            }
        } catch {
            FileLogger.log("database open FAILED: \(error.localizedDescription)")
        }
    }

    private func setupPipeline() {
        eventTapEngine.privacyMode = settings.privacyMode

        // 事件流：聚合 + 缓冲落库 + 疲劳活动信号
        eventTapEngine.onEvent = { [weak self] record in
            self?.consumeEvent(record)
        }
        // 光标轮询采样点走同一消费管道（已在 eventQueue 上回调）
        cursorPoller.onSample = { [weak self] record in
            self?.consumeEvent(record)
        }
        eventTapEngine.onTapCreateFailed = { [weak self] in
            guard let self else { return }
            self.isCaptureRunning = false
            FileLogger.log("event tap create FAILED → show permission guide")
            _ = self.permissionManager.refreshStatus()
            self.showPermissionGuide()
        }

        // 落库管道
        buffer.onFlush = { [weak self] batch in
            guard let self, let repo = self.repository else { return }
            repo.insertEvents(batch)
            repo.insertTrackPoints(batch)
            // 同步把聚合增量并入 daily_stats（day 与 drain 在事件队列上执行）
            self.eventTapEngine.eventQueue.async {
                self.drainDeltaToRepository()
            }
        }
        buffer.start()

        // 窗口活动
        frontAppTracker.onSession = { [weak self] session in
            self?.repository?.insertAppSession(session)
        }
        frontAppTracker.onHeartbeat = { [weak self] _, _ in
            guard let self else { return }
            let interval = self.settings.appHeartbeatSeconds
            self.eventTapEngine.eventQueue.async {
                self.aggregator.processAppHeartbeat(interval: interval)
            }
            self.fatigue.notifyActivity(source: .app)
        }

        // 疲劳提醒
        fatigue.onShouldRemind = { [weak self] in
            self?.reminder.showReminder()
        }
    }

    /// 统一事件消费（必须在 eventQueue 上调用）
    private func consumeEvent(_ record: InputEventRecord) {
        aggregator.process(record)
        buffer.append(record)
        switch record.kind {
        case .keyDown, .keyUp, .flagsChanged:
            fatigue.notifyActivity(source: .keyboard)
        default:
            fatigue.notifyActivity(source: .mouse)
        }
    }

    /// 将聚合增量并入 daily_stats（必须在 eventQueue 上调用）
    private func drainDeltaToRepository() {
        guard let repo = repository else { return }
        let (day, delta) = aggregator.drainDelta()
        let hasContent = delta.keyCount > 0 || delta.clickCount > 0 || delta.scrollCount > 0
            || delta.moveDistance > 0 || delta.keyDurationMs > 0
            || delta.activeInputSeconds > 0 || delta.activeAppSeconds > 0
        if hasContent {
            repo.mergeDailyStats(day: day, delta: delta)
        }
    }

    /// 退出前收尾：停止采集、同步等待所有待写数据落库
    func shutdown() {
        cursorPoller.stop()
        eventTapEngine.stop()
        buffer.stop()          // 同步触发最后一次 flush → 队列化写入
        frontAppTracker.stop() // 结束当前会话 → 队列化写入
        eventTapEngine.eventQueue.sync {
            drainDeltaToRepository()
        }
        repository?.barrierSync()  // 屏障等待全部异步写入完成
        FileLogger.log("shutdown flush done")
    }

    private func setupPermissions() {
        let axOK = permissionManager.checkAccessibility(prompt: true)
        let imOK = permissionManager.checkInputMonitoring()
        if !imOK {
            // 触发系统授权弹窗（仅首次），并把本应用登记进"输入监控"列表
            permissionManager.requestInputMonitoring()
        }
        _ = permissionManager.refreshStatus()
        FileLogger.log("permission preflight: AX=\(axOK) InputMonitoring=\(imOK)")

        if axOK && imOK {
            startCapture()
        } else {
            permissionManager.pollUntilAllGranted { [weak self] in
                self?.startCapture()
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
                self?.showPermissionGuide()
            }
        }
    }

    private func startCapture() {
        guard !isCaptureRunning else { return }
        let tapOK = eventTapEngine.start()
        cursorPoller.start()
        frontAppTracker.start()
        isCaptureRunning = tapOK
        FileLogger.log("startCapture: tap=\(tapOK)")
    }

    // MARK: - 实时快照

    private var snapshotTick = 0

    private func startSnapshotTimer() {
        let timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.snapshotTick += 1
            let shouldDrain = self.snapshotTick % 30 == 0  // 每 30s 无条件落库一次，防止空闲时数据滞留内存
            self.eventTapEngine.eventQueue.async {
                let snap = self.aggregator.snapshot()
                if shouldDrain {
                    self.drainDeltaToRepository()
                }
                if self.snapshotTick % 60 == 0 {
                    FileLogger.log("status: key=\(snap.keyCount) click=\(snap.clickCount) scroll=\(snap.scrollCount) move=\(Int(snap.moveDistance))px active=\(Int(snap.activeInputSeconds))s appActive=\(Int(snap.activeAppSeconds))s")
                }
                DispatchQueue.main.async {
                    self.todayKeyCount = snap.keyCount
                    self.todayClickCount = snap.clickCount
                    self.todayScrollCount = snap.scrollCount
                    self.todayMoveDistance = snap.moveDistance
                    // 活动时间 = 输入活动时间（app 心跳时长与输入重复计算，不再叠加）
                    self.todayActiveSeconds = snap.activeInputSeconds
                    // 跨天则基线清零重算
                    if snap.day != self.interactionBaselineDay {
                        self.interactionBaseline = 0
                        self.interactionBaselineDay = snap.day
                    }
                    self.todayInteractionSeconds = self.interactionBaseline + snap.interactionSeconds
                }
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        snapshotTimer = timer
    }

    // MARK: - 窗口管理

    /// 统一主面板状态（统计/设置分类切换）
    lazy var panelState = MainPanelState()

    func openStatsPanel() {
        panelState.selection = .stats
        openMainPanel()
    }

    func openSettings() {
        panelState.selection = .settings
        openMainPanel()
    }

    private func openMainPanel() {
        openWindow(id: "main", title: "InputMonitor", size: NSSize(width: 1120, height: 680)) { [weak self] in
            guard let self else { return AnyView(EmptyView()) }
            let viewModel = self.repository.map { StatsViewModel(repository: $0) }
            return AnyView(MainPanelView(
                state: self.panelState,
                statsViewModel: viewModel,
                settings: self.settings,
                appState: self
            ))
        }
    }

    func showPermissionGuide() {
        openWindow(id: "permission", title: "需要授权", size: NSSize(width: 520, height: 420)) { [weak self] in
            guard let self else { return AnyView(EmptyView()) }
            return AnyView(PermissionGuideView(permissionManager: self.permissionManager))
        }
    }

    private func openWindow(id: String, title: String, size: NSSize, content: () -> AnyView) {
        if let existing = windowControllers.first(where: { $0.window?.identifier?.rawValue == id }) {
            existing.window?.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.identifier = NSUserInterfaceItemIdentifier(id)
        window.title = title
        window.contentView = NSHostingView(rootView: content())
        window.center()
        window.isReleasedWhenClosed = false
        let controller = NSWindowController(window: window)
        windowControllers.append(controller)
        controller.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

}

/// 权限引导视图：实时显示两项权限状态，授权后自动开始采集
struct PermissionGuideView: View {
    @ObservedObject var permissionManager: PermissionManager

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Label("需要以下权限才能开始采集", systemImage: "lock.shield")
                .font(.system(size: 20, weight: .semibold))

            VStack(alignment: .leading, spacing: 12) {
                PermissionRow(
                    icon: "hand.raised.fill",
                    title: "辅助功能",
                    detail: "用于全局监听鼠标/键盘事件、读取前台窗口标题",
                    granted: permissionManager.isAccessibilityGranted
                )
                PermissionRow(
                    icon: "keyboard",
                    title: "输入监控",
                    detail: "键盘事件监听所需；密码输入期间系统自动屏蔽采集",
                    granted: permissionManager.isInputMonitoringGranted
                )
            }

            HStack(spacing: 12) {
                Button("打开辅助功能设置") { permissionManager.openAccessibilitySettings() }
                Button("打开输入监控设置") { permissionManager.openInputMonitoringSettings() }
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("授权后应用会自动开始采集，无需重启。所有数据仅保存在本机，应用不联网。")
                Text("若开关已打开但仍未采集：将该开关关闭再打开（或点 − 移除后重新添加），签名身份变化可能导致旧授权失效。")
            }
            .font(.system(size: 11))
            .foregroundColor(.secondary)
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

private struct PermissionRow: View {
    let icon: String
    let title: String
    let detail: String
    let granted: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon)
                .foregroundColor(Color(red: 0.04, green: 0.52, blue: 1.0))
                .frame(width: 20)
            VStack(alignment: .leading, spacing: 2) {
                HStack {
                    Text(title).font(.system(size: 14, weight: .medium))
                    Text(granted ? "已授权" : "未授权")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundColor(granted ? Color(red: 0.19, green: 0.65, blue: 0.27) : .white)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(
                            granted ? Color(red: 0.19, green: 0.82, blue: 0.35).opacity(0.2) : Color(red: 1.0, green: 0.27, blue: 0.23),
                            in: Capsule()
                        )
                }
                Text(detail)
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
            }
        }
    }
}
