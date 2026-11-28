import Foundation
import CoreGraphics
import ApplicationServices

/// Turns InputEvents from the client into real macOS mouse/keyboard events.
/// Needs the Accessibility permission. Relative mouse events carry raw deltas so that
/// games that capture the cursor (Minecraft, Roblox mouse-lock) receive proper movement.
final class InputInjector {
    private let src = CGEventSource(stateID: .hidSystemState)
    private var bounds = CGDisplayBounds(CGMainDisplayID())
    private var pos = CGPoint.zero
    private var heldKeys = Set<UInt16>()
    private var buttons = Set<UInt8>()
    private var lastClick = Date.distantPast
    private var lastClickPos = CGPoint.zero
    private var clickCount: Int64 = 1

    private static let modifierFlags: [UInt16: CGEventFlags] = [
        55: .maskCommand, 54: .maskCommand,
        56: .maskShift, 60: .maskShift,
        58: .maskAlternate, 61: .maskAlternate,
        59: .maskControl, 62: .maskControl,
        63: .maskSecondaryFn
    ]

    private var currentFlags: CGEventFlags {
        var f = CGEventFlags()
        for k in heldKeys { if let m = Self.modifierFlags[k] { f.insert(m) } }
        return f
    }

    func configure(displayID: CGDirectDisplayID) {
        bounds = CGDisplayBounds(displayID)
        if let p = CGEvent(source: nil)?.location, bounds.contains(p) {
            pos = p
        } else {
            pos = CGPoint(x: bounds.midX, y: bounds.midY)
        }
    }

    func handle(_ e: InputEvent) {
        switch e {
        case .mouseRel(let dx, let dy):
            let np = clamp(CGPoint(x: pos.x + CGFloat(dx), y: pos.y + CGFloat(dy)))
            move(to: np, dx: Int64(dx), dy: Int64(dy))

        case .mouseAbs(let x, let y):
            let np = CGPoint(x: bounds.minX + CGFloat(x) / 65535.0 * (bounds.width - 1),
                             y: bounds.minY + CGFloat(y) / 65535.0 * (bounds.height - 1))
            move(to: np, dx: Int64(np.x - pos.x), dy: Int64(np.y - pos.y))

        case .button(let b, let down):
            button(b, down: down)

        case .touch, .gamepad, .gamepadPresence, .mediaSeek:
            break   // macOS has no public API for synthetic touch events; the iPad app emulates touch with mouse input for Mac hosts

        case .scroll(let dx, let dy):
            guard let ev = CGEvent(scrollWheelEvent2Source: src, units: .pixel, wheelCount: 2,
                                   wheel1: Int32(dy), wheel2: Int32(dx), wheel3: 0) else { return }
            ev.flags = currentFlags
            ev.post(tap: .cghidEventTap)

        case .key(let code, let down):
            key(code, down: down)
        }
    }

    /// Release everything that is still held (called when the client disconnects).
    func releaseAll() {
        for b in buttons { button(b, down: false) }
        for k in heldKeys { key(k, down: false) }
        buttons.removeAll(); heldKeys.removeAll()
    }

    // MARK: - Private

    private func clamp(_ p: CGPoint) -> CGPoint {
        CGPoint(x: min(max(p.x, bounds.minX), bounds.maxX - 1),
                y: min(max(p.y, bounds.minY), bounds.maxY - 1))
    }

    private func move(to np: CGPoint, dx: Int64, dy: Int64) {
        pos = np
        let type: CGEventType
        let btn: CGMouseButton
        if buttons.contains(0) { type = .leftMouseDragged; btn = .left }
        else if buttons.contains(1) { type = .rightMouseDragged; btn = .right }
        else if buttons.contains(2) { type = .otherMouseDragged; btn = .center }
        else { type = .mouseMoved; btn = .left }

        guard let ev = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: np, mouseButton: btn) else { return }
        ev.setIntegerValueField(.mouseEventDeltaX, value: dx)
        ev.setIntegerValueField(.mouseEventDeltaY, value: dy)
        ev.flags = currentFlags
        ev.post(tap: .cghidEventTap)
    }

    private func button(_ b: UInt8, down: Bool) {
        if down { buttons.insert(b) } else { buttons.remove(b) }

        let type: CGEventType
        let btn: CGMouseButton
        switch b {
        case 0: type = down ? .leftMouseDown : .leftMouseUp; btn = .left
        case 1: type = down ? .rightMouseDown : .rightMouseUp; btn = .right
        default: type = down ? .otherMouseDown : .otherMouseUp; btn = .center
        }

        if down && b == 0 {
            let near = hypot(pos.x - lastClickPos.x, pos.y - lastClickPos.y) < 6
            if Date().timeIntervalSince(lastClick) < 0.35 && near { clickCount += 1 } else { clickCount = 1 }
            lastClick = Date(); lastClickPos = pos
        }

        guard let ev = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: pos, mouseButton: btn) else { return }
        ev.setIntegerValueField(.mouseEventClickState, value: b == 0 ? clickCount : 1)
        ev.flags = currentFlags
        ev.post(tap: .cghidEventTap)
    }

    private func key(_ code: UInt16, down: Bool) {
        if down { heldKeys.insert(code) } else { heldKeys.remove(code) }
        guard let ev = CGEvent(keyboardEventSource: src, virtualKey: CGKeyCode(code), keyDown: down) else { return }
        if Self.modifierFlags[code] != nil { ev.type = .flagsChanged }
        ev.flags = currentFlags
        ev.post(tap: .cghidEventTap)
    }
}
