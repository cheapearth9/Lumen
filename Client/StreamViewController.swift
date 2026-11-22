import UIKit
import SwiftUI
import Combine

/// Full-screen stream view: shows video, owns input capture, pointer lock and the small overlay menu.
final class StreamViewController: UIViewController {
    private let client: StreamClient
    private let input: InputCapture
    private let videoView = VideoView()
    private let statsLabel = UILabel()
    private let menuButton = UIButton(type: .system)
    private var cancellables = Set<AnyCancellable>()
    private var lastPan = CGPoint.zero

    private var locked = false {
        didSet {
            input.relativeMode = locked
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
        videoView.frame = view.bounds
        videoView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(videoView)

        statsLabel.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        statsLabel.textColor = UIColor.white.withAlphaComponent(0.7)
        statsLabel.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(statsLabel)

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
            menuButton.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8)
        ])
        rebuildMenu()

        client.$statsText.receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.statsLabel.text = $0 }
            .store(in: &cancellables)

        installGestures()

        input.onToggleLock = { [weak self] in self?.locked.toggle() }
        input.start()
        UIApplication.shared.isIdleTimerDisabled = true
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        becomeFirstResponder()
    }

    deinit {
        input.stop()
        UIApplication.shared.isIdleTimerDisabled = false
    }

    private func rebuildMenu() {
        let lock = UIAction(title: locked ? "Release mouse (Ctrl+Opt+L)" : "Capture mouse for games (Ctrl+Opt+L)",
                            image: UIImage(systemName: locked ? "cursorarrow.slash" : "cursorarrow.motionlines"),
                            state: locked ? .on : .off) { [weak self] _ in self?.locked.toggle() }
        let disc = UIAction(title: "Disconnect", image: UIImage(systemName: "xmark.circle"),
                            attributes: .destructive) { [weak self] _ in self?.client.disconnect() }
        menuButton.menu = UIMenu(children: [lock, disc])
    }

    // MARK: Normalised position inside the aspect-fit video rectangle

    private func normalised(_ p: CGPoint) -> (UInt16, UInt16) {
        let bounds = view.bounds
        let vs = client.videoSize
        let scale = min(bounds.width / vs.width, bounds.height / vs.height)
        let w = vs.width * scale, h = vs.height * scale
        let rect = CGRect(x: (bounds.width - w) / 2, y: (bounds.height - h) / 2, width: w, height: h)
        let nx = min(max((p.x - rect.minX) / rect.width, 0), 1)
        let ny = min(max((p.y - rect.minY) / rect.height, 0), 1)
        return (UInt16(nx * 65535), UInt16(ny * 65535))
    }

    private func sendAbs(_ p: CGPoint) {
        let (x, y) = normalised(p)
        client.sendInput(.mouseAbs(x: x, y: y))
    }

    // MARK: Gestures (touch + hovering pointer)

    private func installGestures() {
        let direct: [NSNumber] = [NSNumber(value: UITouch.TouchType.direct.rawValue)]

        let tap = UITapGestureRecognizer(target: self, action: #selector(onTap(_:)))
        tap.allowedTouchTypes = direct

        let twoTap = UITapGestureRecognizer(target: self, action: #selector(onTwoFingerTap(_:)))
        twoTap.numberOfTouchesRequired = 2
        twoTap.allowedTouchTypes = direct

        let pan = UIPanGestureRecognizer(target: self, action: #selector(onPan(_:)))
        pan.minimumNumberOfTouches = 1; pan.maximumNumberOfTouches = 1
        pan.allowedTouchTypes = direct

        let scroll = UIPanGestureRecognizer(target: self, action: #selector(onScroll(_:)))
        scroll.minimumNumberOfTouches = 2; scroll.maximumNumberOfTouches = 2
        scroll.allowedTouchTypes = direct

        let hover = UIHoverGestureRecognizer(target: self, action: #selector(onHover(_:)))

        [tap, twoTap, pan, scroll, hover].forEach { view.addGestureRecognizer($0) }
    }

    @objc private func onTap(_ g: UITapGestureRecognizer) {
        if !locked { sendAbs(g.location(in: view)) }
        client.sendInput(.button(0, down: true))
        client.sendInput(.button(0, down: false))
    }

    @objc private func onTwoFingerTap(_ g: UITapGestureRecognizer) {
        if !locked { sendAbs(g.location(in: view)) }
        client.sendInput(.button(1, down: true))
        client.sendInput(.button(1, down: false))
    }

    @objc private func onPan(_ g: UIPanGestureRecognizer) {
        if locked {
            // Touch acts like a trackpad / camera stick while in game mode.
            let t = g.translation(in: view)
            if g.state == .changed {
                input.addRelative(dx: Float(t.x - lastPan.x) * 1.6, dy: Float(t.y - lastPan.y) * 1.6)
            }
            lastPan = (g.state == .ended || g.state == .cancelled) ? .zero : t
            return
        }
        switch g.state {
        case .began:
            sendAbs(g.location(in: view))
            client.sendInput(.button(0, down: true))
        case .changed:
            sendAbs(g.location(in: view))
        case .ended, .cancelled:
            sendAbs(g.location(in: view))
            client.sendInput(.button(0, down: false))
        default: break
        }
    }

    @objc private func onScroll(_ g: UIPanGestureRecognizer) {
        let t = g.translation(in: view)
        g.setTranslation(.zero, in: view)
        if g.state == .changed {
            client.sendInput(.scroll(dx: Int16(t.x), dy: Int16(t.y)))
        }
    }

    @objc private func onHover(_ g: UIHoverGestureRecognizer) {
        guard !locked, g.state == .began || g.state == .changed else { return }
        sendAbs(g.location(in: view))
    }
}

struct StreamControllerView: UIViewControllerRepresentable {
    let client: StreamClient
    func makeUIViewController(context: Context) -> StreamViewController { StreamViewController(client: client) }
    func updateUIViewController(_ vc: StreamViewController, context: Context) {}
}
