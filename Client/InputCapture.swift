import Foundation
import GameController

/// Hardware mouse + keyboard capture through the GameController framework.
/// Raw mouse deltas are batched at 250 Hz so we don't flood the network.
final class InputCapture {
    private let client: StreamClient
    private let lock = NSLock()
    private var accDX: Float = 0
    private var accDY: Float = 0
    private var timer: DispatchSourceTimer?
    private var observers = [NSObjectProtocol]()
    private var held = Set<GCKeyCode>()

    /// When true, mouse movement is sent as raw deltas (pointer is locked / game mode).
    var relativeMode = false
    /// Ctrl+Option+L toggles pointer lock.
    var onToggleLock: (() -> Void)?

    /// GameController reports Y with "up is positive"; flip so it matches screen coordinates.
    private let invertY: Float = -1
    private let scrollScale: Float = 14

    init(client: StreamClient) { self.client = client }

    func start() {
        let nc = NotificationCenter.default
        observers.append(nc.addObserver(forName: .GCMouseDidConnect, object: nil, queue: .main) { [weak self] n in
            if let m = n.object as? GCMouse { self?.bind(m) }
        })
        observers.append(nc.addObserver(forName: .GCKeyboardDidConnect, object: nil, queue: .main) { [weak self] n in
            if let k = n.object as? GCKeyboard { self?.bind(k) }
        })
        GCMouse.mice().forEach(bind)
        if let k = GCKeyboard.coalesced { bind(k) }

        let t = DispatchSource.makeTimerSource(queue: .global(qos: .userInteractive))
        t.schedule(deadline: .now(), repeating: .milliseconds(4), leeway: .milliseconds(1))
        t.setEventHandler { [weak self] in self?.flushMouse() }
        t.resume()
        timer = t
    }

    func stop() {
        timer?.cancel(); timer = nil
        observers.forEach(NotificationCenter.default.removeObserver)
        observers.removeAll()
        for m in GCMouse.mice() {
            m.mouseInput?.mouseMovedHandler = nil
            m.mouseInput?.leftButton.pressedChangedHandler = nil
            m.mouseInput?.rightButton?.pressedChangedHandler = nil
            m.mouseInput?.middleButton?.pressedChangedHandler = nil
            m.mouseInput?.scroll.valueChangedHandler = nil
        }
        GCKeyboard.coalesced?.keyboardInput?.keyChangedHandler = nil
        // Release anything still held so the Mac never gets stuck keys.
        for code in held { if let mac = KeyMap.table[code] { client.sendInput(.key(mac, down: false)) } }
        held.removeAll()
    }

    /// Feed raw deltas from touch (trackpad-style) when no hardware mouse is used.
    func addRelative(dx: Float, dy: Float) {
        lock.lock(); accDX += dx; accDY += dy; lock.unlock()
    }

    // MARK: Mouse

    private func bind(_ mouse: GCMouse) {
        guard let input = mouse.mouseInput else { return }
        input.mouseMovedHandler = { [weak self] _, dx, dy in
            guard let self, self.relativeMode else { return }
            self.addRelative(dx: dx, dy: dy * self.invertY)
        }
        input.leftButton.pressedChangedHandler = { [weak self] _, _, p in self?.client.sendInput(.button(0, down: p)) }
        input.rightButton?.pressedChangedHandler = { [weak self] _, _, p in self?.client.sendInput(.button(1, down: p)) }
        input.middleButton?.pressedChangedHandler = { [weak self] _, _, p in self?.client.sendInput(.button(2, down: p)) }
        input.scroll.valueChangedHandler = { [weak self] _, x, y in
            guard let self else { return }
            let dx = Int16(max(-2000, min(2000, x * self.scrollScale)))
            let dy = Int16(max(-2000, min(2000, y * self.scrollScale)))
            if dx != 0 || dy != 0 { self.client.sendInput(.scroll(dx: dx, dy: dy)) }
        }
    }

    private func flushMouse() {
        lock.lock()
        let dx = accDX, dy = accDY
        // keep sub-pixel remainder so slow movement isn't lost
        let sx = dx.rounded(.towardZero), sy = dy.rounded(.towardZero)
        accDX = dx - sx; accDY = dy - sy
        lock.unlock()
        if sx != 0 || sy != 0 {
            client.sendInput(.mouseRel(dx: Int16(max(-32000, min(32000, sx))),
                                       dy: Int16(max(-32000, min(32000, sy)))))
        }
    }

    // MARK: Keyboard

    private func bind(_ keyboard: GCKeyboard) {
        keyboard.keyboardInput?.keyChangedHandler = { [weak self] _, _, code, pressed in
            self?.key(code, pressed: pressed)
        }
    }

    private func key(_ code: GCKeyCode, pressed: Bool) {
        if pressed { held.insert(code) } else { held.remove(code) }

        // Local hotkey: Ctrl + Option + L releases / captures the mouse.
        if code == .keyL, held.contains(.leftControl) || held.contains(.rightControl),
           held.contains(.leftAlt) || held.contains(.rightAlt) {
            if pressed { DispatchQueue.main.async { self.onToggleLock?() } }
            return
        }
        if let mac = KeyMap.table[code] { client.sendInput(.key(mac, down: pressed)) }
    }
}
