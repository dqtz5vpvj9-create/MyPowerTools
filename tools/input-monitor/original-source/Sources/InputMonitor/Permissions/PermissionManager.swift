import ApplicationServices
import AppKit
import CoreGraphics
import Foundation

/// 权限管理：辅助功能（事件监听 + AX 窗口标题）与输入监控（键盘事件）
/// - 双权限实时状态（引导窗展示）
/// - 轮询直到两项都授权后回调一次
final class PermissionManager: ObservableObject {
    @Published private(set) var isAccessibilityGranted = false
    @Published private(set) var isInputMonitoringGranted = false

    var allGranted: Bool { isAccessibilityGranted && isInputMonitoringGranted }

    /// 辅助功能是否已授权。prompt=true 时会触发系统授权弹窗（仅首次）。
    func checkAccessibility(prompt: Bool) -> Bool {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        return AXIsProcessTrustedWithOptions([key: prompt] as CFDictionary)
    }

    /// 输入监控是否已授权（macOS 10.15+ 有官方预检 API）
    func checkInputMonitoring() -> Bool {
        CGPreflightListenEventAccess()
    }

    /// 触发输入监控系统授权弹窗（仅首次有效），调用后系统设置列表会出现本应用
    func requestInputMonitoring() {
        CGRequestListenEventAccess()
    }

    /// 刷新一次两项状态（返回是否全部授权）
    @discardableResult
    func refreshStatus(promptAX: Bool = false) -> Bool {
        let ax = checkAccessibility(prompt: promptAX)
        let im = checkInputMonitoring()
        DispatchQueue.main.async {
            self.isAccessibilityGranted = ax
            self.isInputMonitoringGranted = im
        }
        return ax && im
    }

    func openAccessibilitySettings() {
        openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
    }

    func openInputMonitoringSettings() {
        openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")
    }

    private func openSettings(_ urlString: String) {
        if let url = URL(string: urlString) {
            NSWorkspace.shared.open(url)
        }
    }

    /// 轮询检测两项权限，全部授权后回调一次并停止。
    @discardableResult
    func pollUntilAllGranted(every interval: TimeInterval = 2.0,
                             onGranted: @escaping () -> Void) -> Timer {
        let timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] timer in
            guard let self else { timer.invalidate(); return }
            if self.refreshStatus() {
                FileLogger.log("permissions granted (AX + InputMonitoring), start capture")
                timer.invalidate()
                DispatchQueue.main.async(execute: onGranted)
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        return timer
    }
}
