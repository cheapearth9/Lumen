import UIKit
import SwiftUI
import Combine

/// Full-screen stream view: shows video, owns input capture (touch engine, hardware mouse / keyboard),
/// pointer lock and the small overlay menu.
final class StreamViewController: UIViewController, TouchEngineDelegate {
    private let client: StreamClient
    private let input: InputCapture
    private lazy var gamepads = GamepadCapture(client: client)
    private let softKeyboard = SoftKeyboardView()
    private let engine = TouchEngine()
    private let touchView = TouchSurfaceView()
    private let videoView = VideoView()
    private let statsLabel = UILabel()
    private let toastLabel = UILabel()
    private let menuButton = UIButton(type: .system)
    private var cancellables = Set<AnyCancellable>()

    private var locked = false {
        didSet {
            input.relativeMode = locked
            engine.pointerLocked = locked
            setNeedsUpdateOfPrefersPointerLocked()
            rebuildMenu()
        }
    }

    init(client: StreamClient) {
        self.client = client
        self.input = InputCapture(client: client)
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override var prefersPointerLocked: Bool { locked }
    override var prefersHomeIndicatorAutoHidden: Bool { true }
    override var prefersStatusBarHidden: Bool { true }
    override var canBecomeFirstResponder: Bool { true }

    // Swallow hardware key presses so UIKit doesn't also act on them (GameController already forwards them).
    override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {}
    override func pressesEnded(_ presses: Set<UIPress>, with event: UIPressesEvent?) {}
    override func pressesChanged(_ presses: Set<UIPress>, with event: UIPressesEvent?) {}
    override func pressesCancelled(_ presses: Set<UIPress>, with event: UIPressesEvent?) {}

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black

        engine.delegate = self
        engine.mode = LumenSettings.touchMode
        engine.hostHasNativeTouch = client.hostSupportsNativeTouch
        touchView.engine = engine
        touchView.frame = view.bounds
        touchView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(touchView)

        videoView.frame = touchView.bounds
        videoView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        videoView.isUserInteractionEnabled = false
        touchView.addSubview(videoView)

        statsLabel.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        statsLabel.textColor = UIColor.white.withAlphaComponent(0.7)
        statsLabel.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(statsLabel)

        toastLabel.font = .systemFont(ofSize: 14, weight: .medium)
        toastLabel.textColor = .white
        toastLabel.textAlignment = .center
        toastLabel.numberOfLines = 0
        toastLabel.backgroundColor = UIColor.black.withAlphaComponent(0.7)
        toastLabel.layer.cornerRadius = 10
        toastLabel.layer.masksToBounds = true
        toastLabel.alpha = 0
        toastLabel.isUserInteractionEnabled = false
        toastLabel.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(toastLabel)

        var cfg = UIButton.Configuration.filled()
        cfg.image = UIImage(systemName: "ellipsis.circle")
        cfg.baseBackgroundColor = UIColor.black.withAlphaComponent(0.45)
        cfg.baseForegroundColor = .white
        cfg.cornerStyle = .capsule
        menuButton.configuration = cfg
        menuButton.showsMenuAsPrimaryAction = true
        menuButton.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(menuButton)

        NSLayoutConstraint.activate([
            statsLabel.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 12),
            statsLabel.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            menuButton.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12),
            menuButton.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            toastLabel.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            toastLabel.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -24),
            toastLabel.leadingAnchor.constraint(greaterThanOrEqualTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            toastLabel.trailingAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24)
        ])
        rebuildMenu()

        client.$statsText.receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.statsLabel.text = $0 }
            .store(in: &cancellables)
        client.$toast.receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.showToast($0) }
            .store(in: &cancellables)
        // receive(on:) defers delivery, so the client's properties already hold the new values here.
        client.$hostCaps.receive(on: DispatchQueue.main)
            .sink { [weak self] _ in
                guard let self else { return }
                self.engine.hostHasNativeTouch = self.client.hostSupportsNativeTouch
                self.rebuildMenu()
            }
            .store(in: &cancellables)
        client.$hostMode.receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.rebuildMenu() }
            .store(in: &cancellables)

        let hover = UIHoverGestureRecognizer(target: self, action: #selector(onHover(_:)))
        touchView.addGestureRecognizer(hover)

        input.onToggleLock = { [weak self] in self?.locked.toggle() }
        input.start()
        gamepads.start()
        softKeyboard.send = { [weak self] e in self?.client.sendInput(e) }
        softKeyboard.onHidden = { [weak self] in self?.rebuildMenu() }
        view.addSubview(softKeyboard)
        if UserDefaults.standard.bool(forKey: "keyboardAuto") { DispatchQueue.main.async { self.softKeyboard.becomeFirstResponder() } }
        // With a monitor attached the stream plays there fullscreen and this screen is the touch surface.
        NotificationCenter.default.addObserver(self, selector: #selector(externalChanged), name: ExternalDisplay.changed, object: nil)
        externalChanged()
        UIApplication.shared.isIdleTimerDisabled = true
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        becomeFirstResponder()
    }

    @objc private func externalChanged() {
        videoView.isHidden = ExternalDisplay.isConnected
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
        gamepads.stop()
        engine.cancelAll()
        input.stop()
        UIApplication.shared.isIdleTimerDisabled = false
    }

    // MARK: Toast

    private func showToast(_ text: String?) {
        if let text, !text.isEmpty {
            toastLabel.text = "  " + text + "  "
            UIView.animate(withDuration: 0.2) { self.toastLabel.alpha = 1 }
        } else {
            UIView.animate(withDuration: 0.3) { self.toastLabel.alpha = 0 }
        }
    }

    // MARK: Menu

    private func rebuildMenu() {
        let extend = LumenSettings.extend
        let modeInfo = UIAction(title: "Host: \(client.hostModeText)", image: UIImage(systemName: "display"),
                                attributes: .disabled) { _ in }
        let mirror = UIAction(title: "Mirror", image: UIImage(systemName: "rectangle.on.rectangle"),
                              state: extend ? .off : .on) { [weak self] _ in
            self?.client.setExtend(false); self?.rebuildMenu()
        }
        let extendAction = UIAction(title: "Extend", image: UIImage(systemName: "rectangle.badge.plus"),
                                    state: extend ? .on : .off) { [weak self] _ in
            self?.client.setExtend(true); self?.rebuildMenu()
        }
        let displayMenu = UIMenu(title: "", options: .displayInline, children: [modeInfo, mirror, extendAction])

        let soundOn = LumenSettings.audioOn
        let soundAttrs: UIMenuElement.Attributes = client.hostSupportsAudio ? [] : [.disabled]
        let sound = UIAction(title: client.hostSupportsAudio ? "Sound" : "Sound (not available on this host)",
                             image: UIImage(systemName: soundOn ? "speaker.wave.2" : "speaker.slash"),
                             attributes: soundAttrs,
                             state: soundOn ? .on : .off) { [weak self] _ in
            self?.client.setAudio(!LumenSettings.audioOn); self?.rebuildMenu()
        }

        let tm = LumenSettings.touchMode
        let pad = UIAction(title: "Touchpad", image: UIImage(systemName: "rectangle.and.hand.point.up.left"),
                           state: tm == .touchpad ? .on : .off) { [weak self] _ in
            LumenSettings.touchMode = .touchpad; self?.engine.mode = .touchpad; self?.rebuildMenu()
        }
        let screen = UIAction(title: "Touchscreen", image: UIImage(systemName: "hand.tap"),
                              state: tm == .touchscreen ? .on : .off) { [weak self] _ in
            LumenSettings.touchMode = .touchscreen; self?.engine.mode = .touchscreen; self?.rebuildMenu()
        }
        let touchMenu = UIMenu(title: "", options: .displayInline, children: [pad, screen])

        let lock = UIAction(title: locked ? "Release mouse (Ctrl+Opt+L)" : "Capture mouse for games (Ctrl+Opt+L)",
                            image: UIImage(systemName: locked ? "cursorarrow.slash" : "cursorarrow.motionlines"),
                            state: locked ? .on : .off) { [weak self] _ in self?.locked.toggle() }
        let disc = UIAction(title: "Disconnect", image: UIImage(systemName: "xmark.circle"),
                            attributes: .destructive) { [weak self] _ in self?.client.disconnect() }
        let kbShown = softKeyboard.isFirstResponder
        let keyboard = UIAction(title: kbShown ? "Hide keyboard" : "Show keyboard", image: UIImage(systemName: "keyboard"),
                                state: kbShown ? .on : .off) { [weak self] _ in
            guard let self else { return }
            if self.softKeyboard.isFirstResponder { _ = self.softKeyboard.resignFirstResponder() }
            else { _ = self.softKeyboard.becomeFirstResponder() }
            self.rebuildMenu()
        }
        menuButton.menu = UIMenu(children: [displayMenu, sound, touchMenu, keyboard, lock, disc])
    }

    // MARK: Geometry

    /// The aspect-fit video rectangle inside the touch view.
    private func videoRect() -> CGRect {
        let bounds = touchView.bounds
        let vs = client.videoSize
        guard vs.width > 0, vs.height > 0, bounds.width > 0, bounds.height > 0 else { return bounds }
        let scale = min(bounds.width / vs.width, bounds.height / vs.height)
        let w = vs.width * scale, h = vs.height * scale
        return CGRect(x: (bounds.width - w) / 2, y: (bounds.height - h) / 2, width: w, height: h)
    }

    private func normalised(_ p: CGPoint) -> (UInt16, UInt16) {
        let rect = videoRect()
        guard rect.width > 0, rect.height > 0 else { return (0, 0) }
        let nx = min(max((p.x - rect.minX) / rect.width, 0), 1)
        let ny = min(max((p.y - rect.minY) / rect.height, 0), 1)
        return (UInt16(nx * 65535), UInt16(ny * 65535))
    }

    // MARK: TouchEngineDelegate

    func engineEmit(_ event: InputEvent) { client.sendInput(event) }

    func engineMoveCursor(dx: Float, dy: Float) { input.addRelative(dx: dx, dy: dy) }

    func engineNormalise(_ point: CGPoint) -> (UInt16, UInt16) { normalised(point) }

    func engineSourcePixelsPerPoint() -> CGFloat {
        let rect = videoRect()
        let src = client.hostSourceSize.width > 0 ? client.hostSourceSize.width : client.videoSize.width
        guard rect.width > 0 else { return 1 }
        return src / rect.width
    }

    // MARK: Hovering pointer (iPad trackpad / mouse, Apple Pencil hover)

    @objc private func onHover(_ g: UIHoverGestureRecognizer) {
        guard !locked, g.state == .began || g.state == .changed else { return }
        let (x, y) = normalised(g.location(in: touchView))
        client.sendInput(.mouseAbs(x: x, y: y))
    }
}

struct StreamControllerView: UIViewControllerRepresentable {
    let client: StreamClient
    func makeUIViewController(context: Context) -> StreamViewController { StreamViewController(client: client) }
    func updateUIViewController(_ vc: StreamViewController, context: Context) {}
}
