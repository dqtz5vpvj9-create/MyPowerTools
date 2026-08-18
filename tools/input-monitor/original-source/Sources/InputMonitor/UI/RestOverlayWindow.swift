import AppKit
import SwiftUI

/// 全屏休息提醒的内容模型（多窗口共享）
final class RestOverlayModel: ObservableObject {
    @Published var remainingSeconds: Int
    let totalSeconds: Int

    init(totalSeconds: Int) {
        self.totalSeconds = totalSeconds
        self.remainingSeconds = totalSeconds
    }

    var timeString: String {
        let m = remainingSeconds / 60
        let s = remainingSeconds % 60
        return String(format: "%02d:%02d", m, s)
    }
}

/// 全屏休息提醒视图：渐变白色遮罩 + 居中倒计时 + 下方跳过按钮
struct RestOverlayView: View {
    @ObservedObject var model: RestOverlayModel
    let onSkip: () -> Void

    @State private var backgroundOpacity: Double = 0
    @State private var contentOpacity: Double = 0

    var body: some View {
        ZStack {
            // 渐变白色遮罩：0 → 0.85
            Color.white.opacity(backgroundOpacity)
                .ignoresSafeArea()

            VStack(spacing: 0) {
                Spacer()

                Image(systemName: "cup.and.saucer.fill")
                    .font(.system(size: 40, weight: .light))
                    .foregroundColor(Color(red: 0.37, green: 0.36, blue: 0.90).opacity(0.8))
                    .padding(.bottom, 24)

                Text("该休息了")
                    .font(.system(size: 28, weight: .semibold))
                    .foregroundColor(.black.opacity(0.75))
                    .padding(.bottom, 12)

                Text(model.timeString)
                    .font(.system(size: 96, weight: .ultraLight, design: .rounded))
                    .monospacedDigit()
                    .foregroundColor(.black.opacity(0.85))
                    .padding(.bottom, 8)

                Text("起身活动一下，看看远处")
                    .font(.system(size: 15, weight: .regular))
                    .foregroundColor(.black.opacity(0.45))

                Spacer()

                Button(action: onSkip) {
                    Text("跳过本次")
                        .font(.system(size: 15, weight: .medium))
                        .foregroundColor(.black.opacity(0.6))
                        .padding(.horizontal, 32)
                        .padding(.vertical, 10)
                        .background(Color.black.opacity(0.06), in: Capsule())
                }
                .buttonStyle(.plain)
                .padding(.bottom, 64)
            }
            .opacity(contentOpacity)
        }
        .onAppear {
            withAnimation(.easeInOut(duration: 1.2)) {
                backgroundOpacity = 0.85
            }
            withAnimation(.easeInOut(duration: 0.8).delay(0.4)) {
                contentOpacity = 1
            }
        }
    }
}

/// 全屏无边框窗口（覆盖单个屏幕）
final class RestOverlayWindow: NSWindow {
    init(screen: NSScreen, contentView: NSView) {
        super.init(
            contentRect: screen.frame,
            styleMask: .borderless,
            backing: .buffered,
            defer: false
        )
        setFrame(screen.frame, display: true)
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        self.contentView = contentView
    }
}
