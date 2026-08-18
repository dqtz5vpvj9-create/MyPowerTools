import Foundation

/// 鼠标移动采样器：距离/时间双阈值节流，并累计位移距离
final class EventSampler {
    /// 距上一采样点的最小距离（px）
    var minDistance: Double
    /// 距上一采样点的最小时间间隔（ns）
    var minIntervalNs: UInt64

    private var lastSampleX: Double?
    private var lastSampleY: Double?
    private var lastSampleTs: UInt64 = 0
    private var lastRawX: Double?
    private var lastRawY: Double?
    /// 自上一采样点以来累计的位移（采样时透出并清零，保证距离不丢失）
    private var pendingDelta: Double = 0

    init(minDistance: Double = 30, minIntervalNs: UInt64 = 50_000_000) {
        self.minDistance = minDistance
        self.minIntervalNs = minIntervalNs
    }

    /// 输入一个原始移动点，返回 (是否采样, 自上一采样点累计位移)
    /// 静止（位移 < 0.5px）时不采样，避免轮询模式下产生大量重复点
    func feed(x: Double, y: Double, timestampNs: UInt64) -> (sampled: Bool, moveDelta: Double) {
        if let lx = lastRawX, let ly = lastRawY {
            pendingDelta += ((x - lx) * (x - lx) + (y - ly) * (y - ly)).squareRoot()
        }
        lastRawX = x
        lastRawY = y

        guard let sx = lastSampleX, let sy = lastSampleY else {
            lastSampleX = x; lastSampleY = y; lastSampleTs = timestampNs
            let delta = pendingDelta
            pendingDelta = 0
            return (true, delta)
        }

        // 静止：自上一采样点以来几乎无位移，跳过
        guard pendingDelta >= 0.5 else { return (false, 0) }

        let distFromSample = ((x - sx) * (x - sx) + (y - sy) * (y - sy)).squareRoot()
        let elapsed = timestampNs &- lastSampleTs
        if distFromSample >= minDistance || elapsed >= minIntervalNs {
            lastSampleX = x; lastSampleY = y; lastSampleTs = timestampNs
            let delta = pendingDelta
            pendingDelta = 0
            return (true, delta)
        }
        return (false, 0)
    }

    func reset() {
        lastSampleX = nil; lastSampleY = nil; lastSampleTs = 0
        lastRawX = nil; lastRawY = nil
        pendingDelta = 0
    }
}
