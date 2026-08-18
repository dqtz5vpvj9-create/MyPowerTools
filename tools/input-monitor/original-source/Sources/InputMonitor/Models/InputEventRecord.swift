import Foundation

/// 统一采集事件模型（聚合与落库共用）
struct InputEventRecord {
    enum Kind: String {
        case keyDown          // 按键按下（ARepeat=0，计为新按键）
        case keyUp            // 按键抬起（与 keyDown 配对算按住时长）
        case flagsChanged     // 修饰键变化（计入键盘活动，不计按键次数）
        case leftClick        // 左键按下
        case rightClick       // 右键按下
        case scroll           // 滚轮
        case mouseMoveSample  // 鼠标移动采样点（已节流）
    }

    let kind: Kind
    let timestampNs: UInt64   // CGEvent.timestamp，纳秒（用于配对计算）
    let wallTime: Date        // 墙上时间（落库与统计）
    let x: Double?            // 鼠标事件屏幕坐标
    let y: Double?
    let keyCode: Int64?       // 键盘事件键码
    let characters: String?   // 按键字符（隐私模式下为 nil）
    let modifiers: UInt64     // CGEventFlags 原始值
    let scrollDelta: Int64    // 滚轮行数（行滚动模式）
    let isAutoRepeat: Bool    // keyDown 是否为长按重复
    let moveDelta: Double     // mouseMoveSample 与上一采样点的位移（px）

    /// 是否构成"用户活动"信号（供疲劳引擎/活动时间统计）
    var isActivity: Bool { true }
}
