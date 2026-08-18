import AppKit
import CoreGraphics
import Foundation

/// 光标位置轮询器：以固定频率在后台队列读取光标位置，采样后输出轨迹点
/// - 替代 CGEventTap 监听 mouseMoved：轮询不介入事件分发，对系统鼠标响应零影响
/// - 静止时不产生采样点（EventSampler 内部判断）
final class CursorPoller {
    /// 采样点输出（在 queue 上回调），record.kind 恒为 mouseMoveSample
    var onSample: ((InputEventRecord) -> Void)?

    private let queue: DispatchQueue
    private let pollIntervalNs: UInt64
    private var sampler: EventSampler
    private var timer: DispatchSourceTimer?
    private(set) var isRunning = false

    /// - Parameters:
    ///   - queue: 轮询执行的串行队列（复用 eventQueue，与事件消费保持顺序）
    ///   - pollIntervalMs: 轮询间隔（默认 33ms ≈ 30Hz，轨迹足够平滑）
    ///   - sampleMinDistance: 采样最小位移（px，来自设置）
    init(queue: DispatchQueue, pollIntervalMs: UInt64 = 33, sampleMinDistance: Double = 30) {
        self.queue = queue
        self.pollIntervalNs = pollIntervalMs * 1_000_000
        self.sampler = EventSampler(minDistance: sampleMinDistance, minIntervalNs: 50_000_000)
    }

    func start() {
        guard !isRunning else { return }
        isRunning = true
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: .nanoseconds(Int(pollIntervalNs)), leeway: .milliseconds(10))
        timer.setEventHandler { [weak self] in
            self?.poll()
        }
        timer.resume()
        self.timer = timer
        FileLogger.log("cursor poller started")
    }

    func stop() {
        timer?.cancel()
        timer = nil
        isRunning = false
    }

    /// 更新采样距离（设置页调整后生效）
    func updateSampleDistance(_ distance: Double) {
        queue.async {
            self.sampler.minDistance = distance
        }
    }

    private func poll() {
        // 优先 CGEvent 坐标（全局显示坐标，左上原点）；失败时回退 NSEvent.mouseLocation 并转换坐标系
        let loc: CGPoint
        if let cgLoc = CGEvent(source: nil)?.location {
            loc = cgLoc
        } else {
            let nsLoc = NSEvent.mouseLocation
            let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
            loc = CGPoint(x: nsLoc.x, y: primaryHeight - nsLoc.y)
        }
        let now = clock_gettime_nsec_np(CLOCK_UPTIME_RAW)

        let (sampled, delta) = sampler.feed(x: loc.x, y: loc.y, timestampNs: now)
        guard sampled else { return }

        onSample?(InputEventRecord(
            kind: .mouseMoveSample, timestampNs: now, wallTime: Date(),
            x: loc.x, y: loc.y, keyCode: nil, characters: nil,
            modifiers: 0, scrollDelta: 0, isAutoRepeat: false, moveDelta: delta
        ))
    }
}
