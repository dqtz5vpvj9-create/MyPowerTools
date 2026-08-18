import AppKit
import SwiftUI

/// 休息提醒窗口生命周期管理：展示（多屏覆盖）→ 倒计时 → 跳过/完成 → 淡出 + 提示音
final class RestReminderController {
    private let fatigue: FatigueEngine
    private let settings: SettingsStore

    private var windows: [NSWindow] = []
    private var model: RestOverlayModel?
    private var countdownTimer: Timer?

    var isShowing: Bool { !windows.isEmpty }

    init(fatigue: FatigueEngine, settings: SettingsStore = .shared) {
        self.fatigue = fatigue
        self.settings = settings
    }

    /// 展示全屏提醒（重复调用安全）
    func showReminder() {
        guard !isShowing else { return }
        fatigue.beginResting()

        let total = settings.restDurationSeconds
        let model = RestOverlayModel(totalSeconds: total)
        self.model = model

        for screen in NSScreen.screens {
            let view = RestOverlayView(model: model) { [weak self] in
                self?.skip()
            }
            let hosting = NSHostingView(rootView: view)
            hosting.frame = screen.frame
            let window = RestOverlayWindow(screen: screen, contentView: hosting)
            window.orderFrontRegardless()
            windows.append(window)
        }

        // 注意：不调用 NSApp.activate——遮罩窗口为 screenSaver 级别自然置顶，
        // 激活 app 会把统计/设置等窗口一并拉到前台，打断用户当前工作
        startCountdown()
    }

    // MARK: - 倒计时

    private func startCountdown() {
        countdownTimer?.invalidate()
        let timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self, let model = self.model else { return }
            if model.remainingSeconds > 1 {
                model.remainingSeconds -= 1
            } else {
                self.finishRest()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        countdownTimer = timer
    }

    // MARK: - 跳过 / 完成

    private func skip() {
        fatigue.skip()          // 值=100、阈值=120，继续累计
        dismiss(animated: true)
    }

    private func finishRest() {
        fatigue.restDone()      // 值=0、阈值恢复 100
        playCompletionSound()   // 提示音
        dismiss(animated: true) // 去除全屏白色（0.5s 淡出）
    }

    private func playCompletionSound() {
        guard settings.soundEnabled else { return }
        guard let sound = NSSound(named: NSSound.Name(settings.soundName)) else { return }
        sound.volume = Float(settings.soundVolume)
        sound.play()
    }

    // MARK: - 关闭

    private func dismiss(animated: Bool) {
        countdownTimer?.invalidate()
        countdownTimer = nil
        model = nil

        let targets = windows
        windows.removeAll()

        guard animated else {
            targets.forEach { $0.orderOut(nil) }
            return
        }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.5
            targets.forEach { $0.animator().alphaValue = 0 }
        } completionHandler: {
            targets.forEach { $0.orderOut(nil) }
        }
    }
}
