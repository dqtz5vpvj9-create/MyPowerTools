import AppKit
import Foundation
import SwiftUI

/// 状态栏控制器（AppKit NSStatusItem）：
/// - 定时器驱动标题刷新（疲劳值% + 当日活动时间），规避 SwiftUI MenuBarExtra label 不刷新的系统问题
/// - 菜单：手动休息 / 暂停提醒（继续记录）/ 状态 / 活动统计 / 设置 / 退出
final class StatusBarController: NSObject {
    private let appState: AppState
    private let statusItem: NSStatusItem
    private var refreshTimer: Timer?

    private var pauseMenuItem: NSMenuItem?
    private var lastRenderedText: String?
    private let chartModel = MenuActivityChartModel()

    init(appState: AppState) {
        self.appState = appState
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()

        setupButton()
        rebuildMenu()
        startRefreshTimer()
        refresh()
    }

    // MARK: - 按钮与标题

    private func setupButton() {
        guard let button = statusItem.button else { return }
        button.imagePosition = .imageLeft
        // 模板图：系统按菜单栏明暗自动着色（深色菜单栏白、浅色菜单栏黑），跟随壁纸自动切换
        let symbol = NSImage(systemSymbolName: "heart.fill", accessibilityDescription: "疲劳值")?
            .withSymbolConfiguration(.init(pointSize: 12, weight: .medium))
        symbol?.isTemplate = true
        button.image = symbol
    }

    private func startRefreshTimer() {
        let timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            self?.refresh()
        }
        RunLoop.main.add(timer, forMode: .common)
        refreshTimer = timer
    }

    private func refresh() {
        let pct = appState.fatigue.percentage
        let active = appState.todayInteractionSeconds
        let h = Int(active) / 3600
        let m = (Int(active) % 3600) / 60
        let text = "\(pct)% · \(h)小时\(m)分"

        if text != lastRenderedText, let button = statusItem.button {
            lastRenderedText = text
            // 不设 foregroundColor：系统按菜单栏明暗使用自适应文字色（烘焙白字会在浅色菜单栏下不可见）
            button.attributedTitle = NSAttributedString(
                string: text,
                attributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .regular)]
            )
        }
        pauseMenuItem?.title = appState.fatigue.isPaused ? "恢复提醒（当前已暂停）" : "暂停提醒（继续记录活动）"
    }

    // MARK: - 菜单

    private func rebuildMenu() {
        let menu = NSMenu()

        // 顶部：今日分小时活动折线图
        // 注意：NSMenuItem 按 view.frame 确定尺寸，NSHostingView 需显式指定 frame
        let chartItem = NSMenuItem()
        let chartHost = NSHostingView(rootView: MenuActivityChartView(model: chartModel))
        chartHost.frame = NSRect(x: 0, y: 0, width: 280, height: 116)
        chartItem.view = chartHost
        menu.addItem(chartItem)

        menu.addItem(.separator())

        let manualRest = NSMenuItem(title: "手动休息", action: #selector(onManualRest), keyEquivalent: "")
        manualRest.target = self
        menu.addItem(manualRest)

        let pause = NSMenuItem(title: "暂停提醒（继续记录活动）", action: #selector(onTogglePause), keyEquivalent: "")
        pause.target = self
        menu.addItem(pause)
        pauseMenuItem = pause

        menu.addItem(.separator())

        let stats = NSMenuItem(title: "活动统计…", action: #selector(onOpenStats), keyEquivalent: "")
        stats.target = self
        menu.addItem(stats)

        let settings = NSMenuItem(title: "设置…", action: #selector(onOpenSettings), keyEquivalent: ",")
        settings.target = self
        menu.addItem(settings)

        menu.addItem(.separator())

        let quit = NSMenuItem(title: "退出 InputMonitor", action: #selector(onQuit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)

        menu.delegate = self
        statusItem.menu = menu
    }

    /// 菜单打开前刷新图表数据（键鼠操作按小时聚合）
    private func reloadChart() {
        guard let repo = appState.repository else { return }
        let today = EventRepository.dayString()
        let currentHour = Calendar.current.component(.hour, from: Date())
        DispatchQueue.global(qos: .userInitiated).async {
            let hourly = repo.hourlyCounts(day: today, kinds: ["keyDown", "leftClick", "rightClick", "scroll"])
            let points = (0...currentHour).map { (hour: $0, count: hourly[$0] ?? 0) }
            DispatchQueue.main.async {
                self.chartModel.points = points
            }
        }
    }

    // MARK: - 动作

    @objc private func onManualRest() {
        appState.fatigue.manualRest()
    }

    @objc private func onTogglePause() {
        appState.fatigue.setPaused(!appState.fatigue.isPaused)
        refresh()
    }

    @objc private func onOpenStats() {
        appState.openStatsPanel()
    }

    @objc private func onOpenSettings() {
        appState.openSettings()
    }

    @objc private func onQuit() {
        NSApp.terminate(nil)
    }
}

extension StatusBarController: NSMenuDelegate {
    func menuWillOpen(_ menu: NSMenu) {
        reloadChart()
    }
}
