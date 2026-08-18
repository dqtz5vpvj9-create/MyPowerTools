import Carbon
import CoreGraphics
import Foundation

/// 键码 → 字符映射（UCKeyTranslate，跟随当前键盘布局）
final class KeyTranslator {
    private var keyboardLayout: UnsafePointer<UCKeyboardLayout>?

    init() {
        reloadLayout()
        DistributedNotificationCenter.default().addObserver(
            forName: Notification.Name(kTISNotifySelectedKeyboardInputSourceChanged as String),
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.reloadLayout()
        }
    }

    private func reloadLayout() {
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let property = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else {
            keyboardLayout = nil
            return
        }
        let data = unsafeBitCast(property, to: CFData.self)
        keyboardLayout = unsafeBitCast(CFDataGetBytePtr(data), to: UnsafePointer<UCKeyboardLayout>.self)
    }

    /// 将 keyCode + 修饰键映射为可打印字符；控制字符/映射失败返回 nil
    func translate(keyCode: UInt16, modifiers: CGEventFlags) -> String? {
        guard let layout = keyboardLayout else { return nil }
        var deadKeyState: UInt32 = 0
        var chars = [UniChar](repeating: 0, count: 8)
        var length = 0
        // UCKeyTranslate 需要 Carbon 风格的修饰键状态（CGEventFlags 高 16 位）
        let modifierState = UInt32((modifiers.rawValue >> 16) & 0xFF)
        let status = UCKeyTranslate(
            layout,
            keyCode,
            UInt16(kUCKeyActionDown),
            modifierState,
            UInt32(LMGetKbdType()),
            OptionBits(kUCKeyTranslateNoDeadKeysMask),
            &deadKeyState,
            chars.count,
            &length,
            &chars
        )
        guard status == noErr, length > 0 else { return nil }
        let result = String(utf16CodeUnits: chars, count: length)
        // 纯控制字符（回车/Tab/ESC 等）不作为"内容"记录
        if result.unicodeScalars.allSatisfy({ $0.value < 0x20 || $0.value == 0x7F }) {
            return nil
        }
        return result
    }
}
