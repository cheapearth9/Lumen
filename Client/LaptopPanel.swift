import UIKit

/// Shown on the iPad's own screen while the stream plays on an AirPlay / HDMI monitor: a laptop keyboard
/// (with a Touch Bar style row that still has Esc and F1-F12), a trackpad with left/right click buttons at the bottom,
/// and palm rests on both sides of the trackpad that ignore touches.
final class LaptopPanelView: UIView {
    /// Keys and clicks go here.
    var send: ((InputEvent) -> Void)?
    /// Cursor movement (host pixels) from the trackpad.
    var moveCursor: ((Float, Float) -> Void)?
    var sourcePixelsPerPoint: (() -> CGFloat)?
    /// Seek the playing video to this position (ms).
    var seek: ((UInt32) -> Void)?
    var nowPlaying: NowPlaying? {
        didSet {
            let was = oldValue?.available == true
            scrubber.update(nowPlaying)
            if was != (nowPlaying?.available == true) { setNeedsLayout() }
        }
    }
    private let scrubber = ScrubberView()

    private struct KeySpec {
        var label: String
        var code: UInt16
        var width: CGFloat = 1
        var small = false
    }

    private let bar = UIView()                       // the "touch bar"
    private var barKeys = [KeyView]()
    private var rows = [[KeyView]]()
    private var rowSpecs = [[KeySpec]]()
    private var arrowUp = KeyView(label: "▲", code: 126, small: true)
    private var arrowDown = KeyView(label: "▼", code: 125, small: true)
    private let padSurface = TouchSurfaceView()
    private let padEngine = TouchEngine()
    private let leftClick = ClickView(button: 0)
    private let rightClick = ClickView(button: 1)
    private let leftRest = RestView()
    private let rightRest = RestView()
    private lazy var padDelegate = PadDelegate(owner: self)

    /// Top inset so the stream menu button (top right) doesn't sit on the keys.
    var topInset: CGFloat = 56 { didSet { setNeedsLayout() } }

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = UIColor(white: 0.55, alpha: 1)            // aluminium-ish deck
        isMultipleTouchEnabled = true

        bar.backgroundColor = .black
        bar.layer.cornerRadius = 10
        addSubview(bar)
        bar.addSubview(scrubber)
        scrubber.onSeek = { [weak self] ms in self?.seek?(ms) }
        // Touch Bar: media / volume controls (Windows host; other hosts ignore them).
        let media: [KeySpec] = [KeySpec(label: "⏮", code: 205, width: 1), KeySpec(label: "⏯", code: 203, width: 1), KeySpec(label: "⏭", code: 204, width: 1),
                                KeySpec(label: "mute", code: 200, width: 1), KeySpec(label: "vol −", code: 201, width: 1), KeySpec(label: "vol +", code: 202, width: 1)]
        for s in media {
            let k = KeyView(label: s.label, code: s.code, small: true, touchBar: true)
            k.widthUnits = s.width
            bar.addSubview(k); barKeys.append(k)
        }

        func row(_ s: [KeySpec]) { rowSpecs.append(s) }
        // Normal function-key row
        let fcodes: [UInt16] = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111]
        row([KeySpec(label: "esc", code: 53, width: 1.4, small: true)] + fcodes.enumerated().map { KeySpec(label: "F\($0.offset + 1)", code: $0.element, small: true) })
        let nums: [(String, UInt16)] = [("`", 50), ("1", 18), ("2", 19), ("3", 20), ("4", 21), ("5", 23), ("6", 22), ("7", 26), ("8", 28), ("9", 25), ("0", 29), ("-", 27), ("=", 24)]
        row(nums.map { KeySpec(label: $0.0, code: $0.1) } + [KeySpec(label: "delete", code: 51, width: 2, small: true)])
        let q: [(String, UInt16)] = [("Q", 12), ("W", 13), ("E", 14), ("R", 15), ("T", 17), ("Y", 16), ("U", 32), ("I", 34), ("O", 31), ("P", 35), ("[", 33), ("]", 30)]
        row([KeySpec(label: "tab", code: 48, width: 1.5, small: true)] + q.map { KeySpec(label: $0.0, code: $0.1) } + [KeySpec(label: "\\", code: 42, width: 1.5)])
        let a: [(String, UInt16)] = [("A", 0), ("S", 1), ("D", 2), ("F", 3), ("G", 5), ("H", 4), ("J", 38), ("K", 40), ("L", 37), (";", 41), ("'", 39)]
        row([KeySpec(label: "caps", code: 57, width: 1.8, small: true)] + a.map { KeySpec(label: $0.0, code: $0.1) } + [KeySpec(label: "return", code: 36, width: 2.2, small: true)])
        let z: [(String, UInt16)] = [("Z", 6), ("X", 7), ("C", 8), ("V", 9), ("B", 11), ("N", 45), ("M", 46), (",", 43), (".", 47), ("/", 44)]
        row([KeySpec(label: "shift", code: 56, width: 2.3, small: true)] + z.map { KeySpec(label: $0.0, code: $0.1) } + [KeySpec(label: "shift", code: 60, width: 2.3, small: true)])
        row([KeySpec(label: "ctrl", code: 59, width: 1.2, small: true), KeySpec(label: "opt", code: 58, width: 1.2, small: true),
             KeySpec(label: "cmd", code: 55, width: 1.4, small: true), KeySpec(label: "", code: 49, width: 5.6),
             KeySpec(label: "cmd", code: 54, width: 1.4, small: true), KeySpec(label: "opt", code: 61, width: 1.2, small: true),
             KeySpec(label: "◀", code: 123, width: 1, small: true), KeySpec(label: "", code: 0, width: 1), KeySpec(label: "▶", code: 124, width: 1, small: true)])
        for r in rowSpecs {
            var keys = [KeyView]()
            for s in r {
                if s.label == "" && s.code == 0 { keys.append(KeyView(label: "", code: 0, small: true)); continue }   // arrow-cell placeholder
                let k = KeyView(label: s.label, code: s.code, small: s.small)
                k.widthUnits = s.width
                addSubview(k); keys.append(k)
            }
            rows.append(keys)
        }
        // the up/down arrows share the cell between left and right
        rows[6][7].isHidden = true
        rows[6][7].widthUnits = 1
        addSubview(arrowUp); addSubview(arrowDown)

        padSurface.backgroundColor = UIColor(white: 0.82, alpha: 1)
        padSurface.layer.cornerRadius = 12
        padSurface.layer.masksToBounds = true
        padSurface.engine = padEngine
        padEngine.mode = .touchpad
        padEngine.delegate = padDelegate
        addSubview(padSurface)
        leftClick.owner = self; rightClick.owner = self
        addSubview(leftClick); addSubview(rightClick)
        addSubview(leftRest); addSubview(rightRest)

        for k in allKeys() { k.owner = self }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    private func allKeys() -> [KeyView] { barKeys + rows.flatMap { $0 } + [arrowUp, arrowDown] }

    fileprivate func emit(_ e: InputEvent) { send?(e) }

    /// Releases anything held (used when the panel is hidden or the stream stops).
    func releaseAll() {
        for k in allKeys() { k.forceUp() }
        leftClick.forceUp(); rightClick.forceUp()
        padEngine.cancelAll()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let margin: CGFloat = 10
        let W = bounds.width - margin * 2
        let top = topInset
        let H = bounds.height - top - margin
        guard W > 0, H > 0 else { return }

        // Row height: touch bar 0.7, five key rows 1.0, trackpad zone gets the rest (min 3.2 units).
        var unit = min(H / (0.7 + 0.62 + 5 + 3.4), W / 15 * 1.05)
        unit = max(unit, 20)
        let gap: CGFloat = max(3, unit * 0.06)
        var y = top

        // Touch bar
        let barH = unit * 0.7
        bar.frame = CGRect(x: margin, y: y, width: W, height: barH)
        if nowPlaying?.available == true {
            // Playing something: [prev play next]  ----scrubber----  [mute vol- vol+]
            scrubber.isHidden = false
            let kw = min(unit * 1.05, (bar.bounds.width - 12) * 0.075)
            for (i, k) in barKeys.enumerated() {
                let x = i < 3 ? 6 + CGFloat(i) * kw : bar.bounds.width - 6 - CGFloat(6 - i) * kw
                k.frame = CGRect(x: x, y: 4, width: kw - gap, height: barH - 8)
            }
            let sx = 6 + 3 * kw + 8
            scrubber.frame = CGRect(x: sx, y: 0, width: bar.bounds.width - 6 - 3 * kw - 8 - sx, height: barH)
        } else {
            scrubber.isHidden = true
            let barTotal = barKeys.reduce(0) { $0 + $1.widthUnits }
            let bw = min(bar.bounds.width - 12, unit * 9)
            var bx: CGFloat = (bar.bounds.width - bw) / 2
            for k in barKeys {
                let w = bw * k.widthUnits / barTotal
                k.frame = CGRect(x: bx, y: 4, width: w - gap, height: barH - 8)
                bx += w
            }
        }
        y += barH + gap * 2

        // Key rows (the function row is shorter)
        for (ri, r) in rows.enumerated() {
            let rh = ri == 0 ? unit * 0.62 : unit
            let total = r.reduce(0) { $0 + $1.widthUnits }
            var x = margin
            for k in r {
                let w = W * k.widthUnits / total
                k.frame = CGRect(x: x, y: y, width: w - gap, height: rh - gap)
                x += w
            }
            y += rh
        }
        // Up/down arrows: halves of the placeholder cell
        let cell = rows[6][7].frame
        arrowUp.frame = CGRect(x: cell.minX, y: cell.minY, width: cell.width, height: cell.height / 2 - 1)
        arrowDown.frame = CGRect(x: cell.minX, y: cell.minY + cell.height / 2 + 1, width: cell.width, height: cell.height / 2 - 1)

        // Trackpad zone: pad in the middle, palm rests left and right
        y += gap * 2
        let zoneH = max(40, bounds.height - margin - y)
        let padW = min(W * 0.46, zoneH * 1.7)
        let padX = margin + (W - padW) / 2
        let btnH = min(zoneH * 0.24, unit * 0.95)
        padSurface.frame = CGRect(x: padX, y: y, width: padW, height: zoneH - btnH - gap)
        leftClick.frame = CGRect(x: padX, y: y + zoneH - btnH, width: padW / 2 - 1, height: btnH)
        rightClick.frame = CGRect(x: padX + padW / 2 + 1, y: y + zoneH - btnH, width: padW / 2 - 1, height: btnH)
        leftRest.frame = CGRect(x: margin, y: y, width: padX - margin - gap * 2, height: zoneH)
        rightRest.frame = CGRect(x: padX + padW + gap * 2, y: y, width: bounds.width - margin - (padX + padW + gap * 2), height: zoneH)
    }

    // The trackpad's engine reports in host pixels; the pad is small, so scale the movement up.
    private final class PadDelegate: TouchEngineDelegate {
        weak var owner: LaptopPanelView?
        init(owner: LaptopPanelView) { self.owner = owner }
        func engineEmit(_ event: InputEvent) { owner?.send?(event) }
        func engineMoveCursor(dx: Float, dy: Float) { owner?.moveCursor?(dx * 2.2, dy * 2.2) }
        func engineNormalise(_ point: CGPoint) -> (UInt16, UInt16) { (0, 0) }
        func engineSourcePixelsPerPoint() -> CGFloat { owner?.sourcePixelsPerPoint?() ?? 1 }
    }
}

// MARK: - Key

private final class KeyView: UIView {
    weak var owner: LaptopPanelView?
    var widthUnits: CGFloat = 1
    private let code: UInt16
    private let label = UILabel()
    private var isDown = false
    private let touchBar: Bool

    init(label text: String, code: UInt16, small: Bool = false, touchBar: Bool = false) {
        self.code = code
        self.touchBar = touchBar
        super.init(frame: .zero)
        isMultipleTouchEnabled = false
        layer.cornerRadius = 6
        label.text = text
        label.textAlignment = .center
        label.adjustsFontSizeToFitWidth = true
        label.minimumScaleFactor = 0.5
        label.font = .systemFont(ofSize: small ? 12 : 17, weight: .medium)
        addSubview(label)
        paint()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func layoutSubviews() {
        super.layoutSubviews()
        label.frame = bounds.insetBy(dx: 2, dy: 0)
    }

    private func paint() {
        if touchBar {
            backgroundColor = isDown ? UIColor(white: 0.45, alpha: 1) : UIColor(white: 0.17, alpha: 1)
            label.textColor = .white
        } else {
            backgroundColor = isDown ? UIColor(white: 0.45, alpha: 1) : UIColor(white: 0.12, alpha: 1)
            label.textColor = UIColor(white: 0.92, alpha: 1)
        }
    }

    private func press() {
        guard !isDown else { return }
        isDown = true; paint()
        owner?.emit(.key(code, down: true))
    }
    func forceUp() { release() }
    private func release() {
        guard isDown else { return }
        isDown = false; paint()
        owner?.emit(.key(code, down: false))
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) { press() }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) { release() }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { release() }
}

// MARK: - Click buttons & palm rests

private final class ClickView: UIView {
    weak var owner: LaptopPanelView?
    private let button: UInt8
    private var isDown = false

    init(button: UInt8) {
        self.button = button
        super.init(frame: .zero)
        backgroundColor = UIColor(white: 0.74, alpha: 1)
        layer.cornerRadius = 8
        isMultipleTouchEnabled = false
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    func forceUp() { release() }
    private func release() {
        guard isDown else { return }
        isDown = false; backgroundColor = UIColor(white: 0.74, alpha: 1)
        owner?.emit(.button(button, down: false))
    }
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard !isDown else { return }
        isDown = true; backgroundColor = UIColor(white: 0.6, alpha: 1)
        owner?.emit(.button(button, down: true))
    }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) { release() }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { release() }
}

/// Soaks up touches so a resting palm does nothing.
private final class RestView: UIView {
    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = UIColor(white: 0.5, alpha: 1)
        layer.cornerRadius = 10
        isUserInteractionEnabled = true
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {}
    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {}
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {}
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {}
}


// MARK: - Touch Bar scrubber (macOS style: elapsed time, slider, remaining time)

private final class ScrubberView: UIView {
    var onSeek: ((UInt32) -> Void)?
    private let elapsed = UILabel(), remaining = UILabel()
    private let track = UIView(), fill = UIView(), thumb = UIView()
    private var info: NowPlaying?
    private var dragging = false
    private var dragFraction: CGFloat = 0
    private var lastSent = Date.distantPast
    private var timer: Timer?

    override init(frame: CGRect) {
        super.init(frame: frame)
        for l in [elapsed, remaining] {
            l.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
            l.textColor = UIColor(white: 0.85, alpha: 1)
            addSubview(l)
        }
        remaining.textAlignment = .right
        track.backgroundColor = UIColor(white: 0.3, alpha: 1)
        fill.backgroundColor = UIColor(white: 0.9, alpha: 1)
        thumb.backgroundColor = .white
        thumb.layer.shadowColor = UIColor.black.cgColor
        thumb.layer.shadowOpacity = 0.5
        thumb.layer.shadowRadius = 2
        thumb.layer.shadowOffset = .zero
        for v in [track, fill, thumb] { addSubview(v) }
        isMultipleTouchEnabled = false
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    func update(_ np: NowPlaying?) {
        info = np
        if np?.available == true {
            if timer == nil { timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.refresh() } }
        } else { timer?.invalidate(); timer = nil }
        refresh()
    }

    private static func fmt(_ ms: Double) -> String {
        let t = Int(max(0, ms) / 1000)
        let h = t / 3600, m = (t % 3600) / 60, s = t % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
    }

    private var trackFrame: CGRect { CGRect(x: 52, y: bounds.midY - 2, width: max(10, bounds.width - 104), height: 4) }

    private func refresh() {
        guard let np = info, np.available, np.durationMs > 0 else { return }
        let dur = Double(np.durationMs)
        let frac = dragging ? dragFraction : CGFloat(min(1, max(0, np.currentMs / dur)))
        elapsed.text = Self.fmt(Double(frac) * dur)
        remaining.text = "-" + Self.fmt(dur - Double(frac) * dur)
        setNeedsLayout()
        fractionShown = frac
        layoutIfNeeded()
    }
    private var fractionShown: CGFloat = 0

    override func layoutSubviews() {
        super.layoutSubviews()
        elapsed.frame = CGRect(x: 0, y: 0, width: 48, height: bounds.height)
        remaining.frame = CGRect(x: bounds.width - 48, y: 0, width: 48, height: bounds.height)
        let t = trackFrame
        track.frame = t; track.layer.cornerRadius = 2
        fill.frame = CGRect(x: t.minX, y: t.minY, width: t.width * fractionShown, height: t.height); fill.layer.cornerRadius = 2
        let d: CGFloat = dragging ? 22 : 16
        thumb.frame = CGRect(x: t.minX + t.width * fractionShown - d / 2, y: bounds.midY - d / 2, width: d, height: d)
        thumb.layer.cornerRadius = d / 2
    }

    private func fraction(for touch: UITouch) -> CGFloat {
        let t = trackFrame
        return min(1, max(0, (touch.location(in: self).x - t.minX) / t.width))
    }

    private func drag(_ touch: UITouch, final: Bool) {
        guard let np = info, np.available else { return }
        dragFraction = fraction(for: touch)
        if final || Date().timeIntervalSince(lastSent) > 0.2 {
            lastSent = Date()
            onSeek?(UInt32(Double(dragFraction) * Double(np.durationMs)))
        }
        refresh()
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let t = touches.first else { return }
        dragging = true
        drag(t, final: false)
    }
    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) { if let t = touches.first { drag(t, final: false) } }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let t = touches.first { drag(t, final: true) }
        // keep showing the dragged spot briefly so the bar doesn't jump back before the host reports the new position
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in self?.dragging = false; self?.refresh() }
    }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { dragging = false; refresh() }
}
