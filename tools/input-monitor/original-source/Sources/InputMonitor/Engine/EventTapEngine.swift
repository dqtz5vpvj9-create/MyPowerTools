import CoreGraphics
import Foundation

/// CGEventTap 全局输入采集引擎
/// - 只监听低频事件（键盘/点击/滚轮）；鼠标移动由 CursorPoller 后台轮询，避免高频回调拖慢系统
/// - 回调只做原始字段拷贝并派发到串行队列；键码翻译、记录构建等全部在后台队列执行，主线程零负担
/// - 监听 tapDisabled 事件自动重挂
final class EventTapEngine {
    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    /// 事件处理串行队列（聚合器在此队列消费事件；外部读取聚合状态需同步到该队列）
    let eventQueue = DispatchQueue(label: "com.local.inputmonitor.events")

    private let translator = KeyTranslator()

    /// 隐私模式：开启后不映射/不落按键字符
    var privacyMode: Bool = false

    /// 事件输出（在 eventQueue 上同步回调）
    var onEvent: ((InputEventRecord) -> Void)?
    /// tap 创建失败（通常为未授予"辅助功能/输入监控"权限）
    var onTapCreateFailed: (() -> Void)?

    /// tap 回调中拷贝的原始事件字段（纯值类型，拷贝代价极小）
    private struct RawTapEvent {
        let type: CGEventType
        let timestampNs: UInt64
        let wallTime: Date
        let flags: UInt64
        let keyCode: Int64
        let isAutoRepeat: Bool
        let x: Double?
        let y: Double?
        let scrollDelta: Int64
    }

    private static let eventMask: CGEventMask = {
        // 只包含低频事件；mouseMoved/dragged 高频事件由 CursorPoller 轮询替代
        let types: [CGEventType] = [
            .keyDown, .keyUp, .flagsChanged,
            .leftMouseDown, .leftMouseUp,
            .rightMouseDown, .rightMouseUp,
            .scrollWheel
        ]
        return types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
    }()

    @discardableResult
    func start() -> Bool {
        stop()
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: Self.eventMask,
            callback: inputMonitorEventTapCallback,
            userInfo: refcon
        ) else {
            FileLogger.log("event tap create FAILED (permission missing?)")
            DispatchQueue.main.async { self.onTapCreateFailed?() }
            return false
        }
        eventTap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        FileLogger.log("event tap created (low-frequency events only)")
        return true
    }

    func stop() {
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
            runLoopSource = nil
        }
        if let tap = eventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
            CFMachPortInvalidate(tap)
            eventTap = nil
        }
    }

    fileprivate func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        // 系统因超时/用户输入自动禁用 tap 后，立即重挂
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap = eventTap {
                CGEvent.tapEnable(tap: tap, enable: true)
                FileLogger.log("event tap re-enabled (was disabled by system)")
            }
            return Unmanaged.passUnretained(event)
        }

        // 回调只拷贝原始字段，立即返回；构建/翻译全部在后台队列执行
        var scrollDelta: Int64 = 0
        if type == .scrollWheel {
            let line = event.getIntegerValueField(.scrollWheelEventPointDeltaAxis1)
            let fixed = event.getIntegerValueField(.scrollWheelEventFixedPtDeltaAxis1)
            scrollDelta = line != 0 ? line : (fixed != 0 ? (fixed > 0 ? 1 : -1) : 0)
        }
        var x: Double?
        var y: Double?
        if type == .leftMouseDown || type == .rightMouseDown {
            let loc = event.location
            x = loc.x
            y = loc.y
        }
        let raw = RawTapEvent(
            type: type,
            timestampNs: event.timestamp,
            wallTime: Date(),
            flags: UInt64(event.flags.rawValue),
            keyCode: event.getIntegerValueField(.keyboardEventKeycode),
            isAutoRepeat: event.getIntegerValueField(.keyboardEventAutorepeat) != 0,
            x: x, y: y,
            scrollDelta: scrollDelta
        )
        eventQueue.async { [weak self] in
            self?.process(raw)
        }
        return Unmanaged.passUnretained(event)
    }

    /// 在 eventQueue 上执行：构建记录（含键码翻译）并输出
    private func process(_ raw: RawTapEvent) {
        guard let record = makeRecord(from: raw) else { return }
        onEvent?(record)
    }

    private func makeRecord(from raw: RawTapEvent) -> InputEventRecord? {
        switch raw.type {
        case .keyDown:
            // UCKeyTranslate 在后台队列执行，不占用主线程
            let chars = privacyMode ? nil : translator.translate(
                keyCode: UInt16(clamping: raw.keyCode),
                modifiers: CGEventFlags(rawValue: raw.flags)
            )
            return InputEventRecord(
                kind: .keyDown, timestampNs: raw.timestampNs, wallTime: raw.wallTime,
                x: nil, y: nil, keyCode: raw.keyCode, characters: chars,
                modifiers: raw.flags, scrollDelta: 0, isAutoRepeat: raw.isAutoRepeat, moveDelta: 0
            )

        case .keyUp:
            return InputEventRecord(
                kind: .keyUp, timestampNs: raw.timestampNs, wallTime: raw.wallTime,
                x: nil, y: nil, keyCode: raw.keyCode, characters: nil,
                modifiers: raw.flags, scrollDelta: 0, isAutoRepeat: false, moveDelta: 0
            )

        case .flagsChanged:
            return InputEventRecord(
                kind: .flagsChanged, timestampNs: raw.timestampNs, wallTime: raw.wallTime,
                x: nil, y: nil, keyCode: raw.keyCode, characters: nil,
                modifiers: raw.flags, scrollDelta: 0, isAutoRepeat: false, moveDelta: 0
            )

        case .leftMouseDown, .rightMouseDown:
            return InputEventRecord(
                kind: raw.type == .leftMouseDown ? .leftClick : .rightClick,
                timestampNs: raw.timestampNs, wallTime: raw.wallTime,
                x: raw.x, y: raw.y, keyCode: nil, characters: nil,
                modifiers: raw.flags, scrollDelta: 0, isAutoRepeat: false, moveDelta: 0
            )

        case .scrollWheel:
            return InputEventRecord(
                kind: .scroll, timestampNs: raw.timestampNs, wallTime: raw.wallTime,
                x: nil, y: nil, keyCode: nil, characters: nil,
                modifiers: raw.flags, scrollDelta: raw.scrollDelta, isAutoRepeat: false, moveDelta: 0
            )

        default:
            return nil
        }
    }
}

/// C 回调入口：不持有引用，事件原样透传（listen-only）
private func inputMonitorEventTapCallback(
    _ proxy: CGEventTapProxy,
    _ type: CGEventType,
    _ event: CGEvent,
    _ refcon: UnsafeMutableRawPointer?
) -> Unmanaged<CGEvent>? {
    guard let refcon else { return Unmanaged.passUnretained(event) }
    let engine = Unmanaged<EventTapEngine>.fromOpaque(refcon).takeUnretainedValue()
    return engine.handle(type, event)
}
