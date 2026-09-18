import CoreGraphics
import Foundation

/// 把 Windows 端回传的输入事件合成为 macOS 的鼠标/键盘事件
final class InputInjector: @unchecked Sendable {
    /// 当前虚拟屏尺寸（用于归一化坐标换算）
    var screenSize: () -> (Int, Int) = { (1920, 1080) }
    /// 虚拟屏在全局桌面坐标里的原点。CGEvent 的 mouseCursorPosition 是全局坐标，
    /// 只给屏内偏移会打到主屏上（虚拟屏通常挂在主屏右侧 x=主屏宽度处）
    var displayOrigin: () -> CGPoint = { .zero }

    private var flags: CGEventFlags = []
    private var leftDown = false
    private var rightDown = false
    private let q = DispatchQueue(label: "input.inject")

    static func hasAccessibility() -> Bool {
        CGPreflightListenEventAccess()
    }

    private func point(nx: Float, ny: Float) -> CGPoint {
        let (w, h) = screenSize()
        let o = displayOrigin()
        let x = o.x + CGFloat(max(0, min(1, nx))) * CGFloat(w)
        let y = o.y + CGFloat(max(0, min(1, ny))) * CGFloat(h)
        return CGPoint(x: x, y: y)
    }

    func move(nx: Float, ny: Float) {
        q.async { self.postMouse(.mouseMoved, nx: nx, ny: ny) }
    }

    func button(_ btn: UInt8, isDown: Bool, nx: Float, ny: Float) {
        q.async {
            switch btn {
            case 0: self.leftDown = isDown
            case 1: self.rightDown = isDown
            default: break
            }
            let t: CGEventType
            switch btn {
            case 0: t = isDown ? .leftMouseDown : .leftMouseUp
            case 1: t = isDown ? .rightMouseDown : .rightMouseUp
            default: t = isDown ? .otherMouseDown : .otherMouseUp
            }
            self.postMouse(t, nx: nx, ny: ny)
        }
    }

    func scroll(dx: Float, dy: Float) {
        q.async {
            // 行单位，正 wheel1 = 向上滚
            guard let e = CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 2,
                                  wheel1: Int32(-dy * 3), wheel2: Int32(dx * 3), wheel3: 0) else { return }
            e.flags = self.flags
            e.post(tap: .cghidEventTap)
        }
    }

    /// Windows VK 码 → macOS 键码（美式布局）
    private static let vkToMac: [UInt16: Int] = [
        // 字母
        0x41: 0,  0x42: 11, 0x43: 8,  0x44: 2,  0x45: 14, 0x46: 3,  0x47: 5,
        0x48: 4,  0x49: 34, 0x4A: 38, 0x4B: 40, 0x4C: 37, 0x4D: 46, 0x4E: 45,
        0x4F: 31, 0x50: 35, 0x51: 12, 0x52: 15, 0x53: 1,  0x54: 17, 0x55: 32,
        0x56: 9,  0x57: 13, 0x58: 7,  0x59: 16, 0x5A: 6,
        // 数字
        0x30: 29, 0x31: 18, 0x32: 19, 0x33: 20, 0x34: 21, 0x35: 23,
        0x36: 22, 0x37: 26, 0x38: 28, 0x39: 25,
        // 符号
        0xBA: 41,  // ; :
        0xBB: 24,  // = +
        0xBC: 43,  // , <
        0xBD: 27,  // - _
        0xBE: 47,  // . >
        0xBF: 44,  // / ?
        0xC0: 50,  // ` ~
        0xDB: 33,  // [ {
        0xDC: 42,  // \ |
        0xDD: 30,  // ] }
        0xDE: 39,  // ' "
        // 编辑/导航
        0x08: 51,  // Backspace
        0x09: 48,  // Tab
        0x0D: 36,  // Enter
        0x1B: 53,  // Esc
        0x20: 49,  // Space
        0x14: 57,  // CapsLock（两端状态可能漂移，再按一次纠正）
        0x21: 116, // Page Up
        0x22: 121, // Page Down
        0x23: 119, // End
        0x24: 115, // Home
        0x25: 123, // ←
        0x26: 126, // ↑
        0x27: 124, // →
        0x28: 125, // ↓
        0x2E: 117, // Delete（前向删除）
        // F 区
        0x70: 122, 0x71: 120, 0x72: 99,  0x73: 118, 0x74: 96,  0x75: 97,
        0x76: 98,  0x77: 100, 0x78: 101, 0x79: 109, 0x7A: 103, 0x7B: 111,
        // 小键盘
        0x60: 83, 0x61: 84, 0x62: 85, 0x63: 86, 0x64: 87, 0x65: 88,
        0x66: 89, 0x67: 91, 0x68: 92, 0x69: 82,
        0x6A: 67, 0x6B: 69, 0x6D: 78, 0x6E: 65, 0x6F: 81,
    ]

    /// Windows 修饰键 → Mac 修饰键（Ctrl→Cmd、Alt→Option、Win→Ctrl、Shift 原样）
    private static let vkModifiers: [UInt16: (CGEventFlags, Int)] = [
        0x10: (.maskShift, 56),     0xA0: (.maskShift, 56),     0xA1: (.maskShift, 56),
        0x11: (.maskCommand, 55),   0xA2: (.maskCommand, 55),   0xA3: (.maskCommand, 55),
        0x12: (.maskAlternate, 58), 0xA4: (.maskAlternate, 58), 0xA5: (.maskAlternate, 58),
        0x5B: (.maskControl, 59),   0x5C: (.maskControl, 59),
    ]

    /// 键盘事件入口（ControlServer 调用）
    func key(_ code: UInt16, isDown: Bool) {
        q.async { self.keySync(code, isDown: isDown) }
    }

    /// VK 码 + down/up（低级钩子成对转发）
    private func keySync(_ code: UInt16, isDown: Bool) {
        if let (flag, vk) = Self.vkModifiers[code] {
            if isDown { flags.insert(flag) } else { flags.remove(flag) }
            if let e = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(vk), keyDown: isDown) {
                e.post(tap: .cghidEventTap)
            }
            return
        }
        guard let vk = Self.vkToMac[code],
              let e = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(vk), keyDown: isDown) else { return }
        e.flags = flags
        e.post(tap: .cghidEventTap)
    }

    private func postMouse(_ t: CGEventType, nx: Float, ny: Float) {
        var t = t
        if t == .mouseMoved {   // 按住移动 = 拖拽
            if leftDown { t = .leftMouseDragged }
            else if rightDown { t = .rightMouseDragged }
        }
        let p = point(nx: nx, ny: ny)
        let btn: CGMouseButton
        switch t {
        case .rightMouseDown, .rightMouseUp, .rightMouseDragged: btn = .right
        case .otherMouseDown, .otherMouseUp, .otherMouseDragged: btn = .center
        default: btn = .left
        }
        let e = CGEvent(mouseEventSource: nil, mouseType: t,
                        mouseCursorPosition: p, mouseButton: btn)
        if t == .otherMouseDown || t == .otherMouseUp {
            e?.setIntegerValueField(CGEventField.mouseEventClickState, value: 1)
        }
        e?.flags = flags
        e?.post(tap: .cghidEventTap)
    }
}
