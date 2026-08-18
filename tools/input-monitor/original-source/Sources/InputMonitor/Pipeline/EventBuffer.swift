import Foundation

/// 事件环形缓冲：定量/定时触发批量 flush，磁盘 I/O 与事件频率解耦
final class EventBuffer {
    private let capacity: Int
    private let flushInterval: TimeInterval
    private var buffer: [InputEventRecord] = []
    private var timer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "com.local.inputmonitor.buffer")

    /// flush 回调（在 queue 上触发）
    var onFlush: (([InputEventRecord]) -> Void)?

    init(capacity: Int = 500, flushInterval: TimeInterval = 5) {
        self.capacity = capacity
        self.flushInterval = flushInterval
    }

    func start() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + flushInterval, repeating: flushInterval)
        timer.setEventHandler { [weak self] in
            self?.flushLocked()
        }
        timer.resume()
        self.timer = timer
    }

    func stop() {
        timer?.cancel()
        timer = nil
        queue.sync { flushLocked() }
    }

    func append(_ record: InputEventRecord) {
        queue.async {
            self.buffer.append(record)
            if self.buffer.count >= self.capacity {
                self.flushLocked()
            }
        }
    }

    func flush() {
        queue.async { self.flushLocked() }
    }

    private func flushLocked() {
        guard !buffer.isEmpty else { return }
        let batch = buffer
        buffer.removeAll(keepingCapacity: true)
        onFlush?(batch)
    }
}
