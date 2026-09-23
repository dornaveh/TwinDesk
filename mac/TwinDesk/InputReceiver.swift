import AppKit
import CoreGraphics

final class InputReceiver {
    private var keys = Set<CGKeyCode>()
    private var buttons = Set<Int>()
    private var point = CGPoint.zero
    private var clicks: [Int: (Double, CGPoint, Int64)] = [:]
    private var caps = false
    var active = false
    private let source = CGEventSource(stateID: .privateState)
    // Windows set-1 physical scan codes to macOS virtual key codes.
    // Character composition and keyboard layout remain macOS's responsibility.
    private let scanMap: [Int: CGKeyCode] = [
        1:53,2:18,3:19,4:20,5:21,6:23,7:22,8:26,9:28,10:25,11:29,12:27,13:24,14:51,15:48,
        16:12,17:13,18:14,19:15,20:17,21:16,22:32,23:34,24:31,25:35,26:33,27:30,28:36,29:59,
        30:0,31:1,32:2,33:3,34:5,35:4,36:38,37:40,38:37,39:41,40:39,41:50,42:56,43:42,
        44:6,45:7,46:8,47:9,48:11,49:45,50:46,51:43,52:47,53:44,54:60,55:67,56:58,57:49,58:57,
        59:122,60:120,61:99,62:118,63:96,64:97,65:98,66:100,67:101,68:109,69:71,
        71:89,72:91,73:92,74:78,75:86,76:87,77:88,78:69,79:83,80:84,81:85,82:82,83:65,86:10,87:103,88:111]
    private let extendedMap: [Int: CGKeyCode] = [28:76,29:62,53:75,56:61,71:115,72:126,73:116,75:123,77:124,79:119,80:125,81:121,82:114,83:117,91:55,92:54]

    func activate() throws {
        guard AXIsProcessTrusted() else { throw BridgeError.message("Enable TwinDesk in macOS Accessibility settings first.") }
        releaseAll()
        point = CGEvent(source: nil)?.location ?? .zero
        caps = CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
        active = true
    }
    private var flags: CGEventFlags {
        var result: CGEventFlags = []
        if keys.contains(56) || keys.contains(60) { result.insert(.maskShift) }
        if keys.contains(59) || keys.contains(62) { result.insert(.maskControl) }
        if keys.contains(58) || keys.contains(61) { result.insert(.maskAlternate) }
        if keys.contains(55) || keys.contains(54) { result.insert(.maskCommand) }
        if caps { result.insert(.maskAlphaShift) }
        return result
    }
    func handle(_ data: Data) throws {
        guard active else { return }
        guard let p = try JSONSerialization.jsonObject(with: data) as? [String: Any], let kind = p["kind"] as? String else { throw BridgeError.message("Invalid input packet.") }
        switch kind {
        case "key":
            guard let scan = p["scan"] as? Int, let down = p["down"] as? Bool, let extended = p["extended"] as? Bool else { throw BridgeError.message("Invalid key packet.") }
            guard let key = (extended ? extendedMap : scanMap)[scan] else { return }
            if down { keys.insert(key) } else { keys.remove(key) }
            if key == 57 && down && p["repeat"] as? Bool != true { caps.toggle() }
            let event = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: down)
            event?.flags = flags
            event?.setIntegerValueField(.keyboardEventAutorepeat, value: p["repeat"] as? Bool == true ? 1 : 0)
            event?.post(tap: .cghidEventTap)
        case "move":
            guard let dx = p["dx"] as? Int, let dy = p["dy"] as? Int, (-32768...32768).contains(dx), (-32768...32768).contains(dy) else { throw BridgeError.message("Invalid pointer movement.") }
            point = constrain(CGPoint(x: point.x + CGFloat(dx), y: point.y + CGFloat(dy)))
            let b = buttons.contains(0) ? 0 : buttons.contains(1) ? 1 : (buttons.first ?? 0)
            let type: CGEventType = buttons.isEmpty ? .mouseMoved : b == 0 ? .leftMouseDragged : b == 1 ? .rightMouseDragged : .otherMouseDragged
            let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: CGMouseButton(rawValue: UInt32(b))!)
            event?.flags = flags; event?.post(tap: .cghidEventTap)
        case "button":
            guard let button = p["button"] as? Int, (0...4).contains(button), let down = p["down"] as? Bool else { throw BridgeError.message("Invalid pointer button.") }
            if down { buttons.insert(button) } else { buttons.remove(button) }
            let now = ProcessInfo.processInfo.systemUptime
            if down {
                let last = clicks[button]
                let count: Int64 = last != nil && now - last!.0 < NSEvent.doubleClickInterval && hypot(point.x - last!.1.x, point.y - last!.1.y) < 5 ? min(last!.2 + 1, 3) : 1
                clicks[button] = (now, point, count)
            }
            postButton(button, down, clicks[button]?.2 ?? 1)
        case "scroll":
            guard let delta = p["delta"] as? Int, (-32768...32768).contains(delta), let horizontal = p["horizontal"] as? Bool else { throw BridgeError.message("Invalid scroll packet.") }
            let amount = Int32(delta / 40)
            let event = CGEvent(scrollWheelEvent2Source: source, units: .line, wheelCount: 2, wheel1: horizontal ? 0 : amount, wheel2: horizontal ? amount : 0, wheel3: 0)
            event?.flags = flags; event?.post(tap: .cghidEventTap)
        default: throw BridgeError.message("Unknown input action.")
        }
    }
    private func postButton(_ b: Int, _ down: Bool, _ count: Int64) {
        let type: CGEventType = b == 0 ? (down ? .leftMouseDown : .leftMouseUp) : b == 1 ? (down ? .rightMouseDown : .rightMouseUp) : (down ? .otherMouseDown : .otherMouseUp)
        let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: CGMouseButton(rawValue: UInt32(b))!)
        event?.flags = flags; event?.setIntegerValueField(.mouseEventClickState, value: count); event?.post(tap: .cghidEventTap)
    }
    func releaseAll() {
        active = false
        let pressed = keys; keys.removeAll()
        for key in pressed { let e = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false); e?.flags = flags; e?.post(tap: .cghidEventTap) }
        for b in buttons { postButton(b, false, 1) }
        buttons.removeAll()
    }
    private func constrain(_ target: CGPoint) -> CGPoint {
        var ids = [CGDirectDisplayID](repeating: 0, count: 16); var count: UInt32 = 0
        guard CGGetActiveDisplayList(16, &ids, &count) == .success, count > 0 else { return target }
        var nearest = target; var distance = CGFloat.infinity
        for id in ids.prefix(Int(count)) {
            let r = CGDisplayBounds(id)
            let p = CGPoint(x: max(r.minX, min(r.maxX - 1, target.x)), y: max(r.minY, min(r.maxY - 1, target.y)))
            let d = hypot(p.x - target.x, p.y - target.y)
            if d < distance { nearest = p; distance = d }
        }
        return nearest
    }
}
