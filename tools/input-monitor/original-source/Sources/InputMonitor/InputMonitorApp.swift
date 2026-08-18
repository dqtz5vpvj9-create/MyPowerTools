import AppKit
import SwiftUI

@main
struct InputMonitorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        // 占位场景：应用主体是状态栏 + 自建窗口，此场景不展示内容
        Settings { EmptyView() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    let appState = AppState()
    private var statusBar: StatusBarController?
    /// 禁用 App Nap：防止后台定时器（轮询/落库/刷新）被系统挂起
    private var activityToken: NSObjectProtocol?

    func applicationDidFinishLaunching(_ notification: Notification) {
        activityToken = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiated, .latencyCritical],
            reason: "持续监测输入活动"
        )
        appState.start()
        statusBar = StatusBarController(appState: appState)
    }

    func applicationWillTerminate(_ notification: Notification) {
        // 退出前同步落库所有滞留数据（会话、聚合增量、缓冲事件）
        appState.shutdown()
    }
}
