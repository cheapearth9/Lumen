import SwiftUI
import UIKit
import Network

@main
struct LumenClientApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        WindowGroup { RootView() }
    }
}

// MARK: - External display (USB-C / Lightning HDMI adapter)

/// When a monitor is plugged in, iPadOS/iOS hands us a second scene for it.
/// Giving it a window means the monitor becomes a real extended display showing the stream
/// instead of a plain mirror of the device screen.
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(_ application: UIApplication,
                     configurationForConnecting connectingSceneSession: UISceneSession,
                     options: UIScene.ConnectionOptions) -> UISceneConfiguration {
        if connectingSceneSession.role == .windowExternalDisplayNonInteractive
            || connectingSceneSession.role.rawValue == "UIWindowSceneSessionRoleExternalDisplay" {
            let config = UISceneConfiguration(name: "External Display", sessionRole: connectingSceneSession.role)
            config.delegateClass = ExternalSceneDelegate.self
            return config
        }
        return UISceneConfiguration(name: nil, sessionRole: connectingSceneSession.role)
    }
}

/// Tracks whether a monitor is attached. While one is, the stream is shown there fullscreen and the
/// device's own screen becomes the touchpad / touchscreen surface.
enum ExternalDisplay {
    static let changed = Notification.Name("LumenExternalDisplayChanged")
    private(set) static var count = 0
    static var isConnected: Bool { count > 0 }
    static func update(_ delta: Int) {
        count = max(0, count + delta)
        NotificationCenter.default.post(name: changed, object: nil)
    }
}

final class ExternalSceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    func sceneDidDisconnect(_ scene: UIScene) { window = nil; ExternalDisplay.update(-1) }

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }
        let vc = ExternalVideoController()
        let w = UIWindow(windowScene: windowScene)
        w.rootViewController = vc
        w.frame = windowScene.screen.bounds
        w.isHidden = false
        window = w
        ExternalDisplay.update(1)
    }
}

final class ExternalVideoController: UIViewController {
    override func loadView() {
        let v = VideoView()
        v.displayLayer.videoGravity = .resizeAspect
        view = v
    }
    override var prefersStatusBarHidden: Bool { true }
}

// MARK: - Root UI

struct RootView: View {
    @StateObject private var client = StreamClient()
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage("fps") private var fps = 60
    @AppStorage("manualHost") private var manualHost = ""

    var body: some View {
        Group {
            if case .streaming = client.state {
                StreamControllerView(client: client)
                    .ignoresSafeArea()
                    .statusBarHidden(true)
                    .persistentSystemOverlays(.hidden)
            } else {
                ConnectView(client: client, fps: $fps, manualHost: $manualHost, makeHello: makeHello)
            }
        }
        .onAppear {
            client.helloProvider = makeHello
            client.start()
        }
        .onChange(of: scenePhase) { phase in
            // The cable listener can be torn down while backgrounded; re-arm it.
            if phase == .active { client.start() }
        }
    }

    /// The size to ask the computer for: the device's own resolution, 1080p, or a custom size.
    private func makeHello() -> HelloInfo {
        let (w, h) = ResolutionSetting.requested()
        return HelloInfo(width: UInt16(w), height: UInt16(h), fps: UInt8(fps), bitrateMbps: 40,
                         flags: LumenSettings.helloFlags())
    }
}

/// Resolution choices: full screen (device original), 16:9 (1080p) or custom.
enum ResolutionSetting {
    static let modeKey = "resMode", widthKey = "resW", heightKey = "resH"

    static func requested() -> (Int, Int) {
        let d = UserDefaults.standard
        switch d.integer(forKey: modeKey) {
        case 1: return (1920, 1080)
        case 2:
            let w = d.object(forKey: widthKey) as? Int ?? 1920, h = d.object(forKey: heightKey) as? Int ?? 1080
            return (min(3840, max(640, w)) & ~1, min(2160, max(360, h)) & ~1)
        default:
            let screens = UIScreen.screens
            let target = screens.count > 1 ? screens[screens.count - 1] : UIScreen.main
            let b = target.nativeBounds
            return (Int(min(3840, max(b.width, b.height))), Int(min(2160, min(b.width, b.height))))
        }
    }
}

struct ResolutionPicker: View {
    @AppStorage(ResolutionSetting.modeKey) private var mode = 0
    @AppStorage(ResolutionSetting.widthKey) private var w = 1920
    @AppStorage(ResolutionSetting.heightKey) private var h = 1080

    var body: some View {
        Picker("Resolution", selection: $mode) {
            Text("Full screen").tag(0)
            Text("16:9 (1080p)").tag(1)
            Text("Custom").tag(2)
        }
        .pickerStyle(.segmented)
        if mode == 2 {
            HStack {
                TextField("Width", value: $w, format: .number.grouping(.never)).keyboardType(.numberPad).multilineTextAlignment(.trailing)
                Text("×")
                TextField("Height", value: $h, format: .number.grouping(.never)).keyboardType(.numberPad)
            }
        }
        Text(mode == 0 ? "The device's own resolution (an attached monitor's resolution when one is plugged in)."
             : mode == 1 ? "1920×1080: lighter on the computer and the network."
             : "Between 640×360 and 3840×2160. The picture keeps the computer screen's shape inside this size.")
            .font(.footnote).foregroundStyle(.secondary)
    }
}

struct ConnectView: View {
    @ObservedObject var client: StreamClient
    @Binding var fps: Int
    @AppStorage("mbpsCable") private var mbpsCable = 100.0
    @AppStorage("mbpsWifi") private var mbpsWifi = 40.0
    @Binding var manualHost: String
    let makeHello: () -> HelloInfo

    @AppStorage(LumenSettings.extendKey) private var extendMode = false
    @AppStorage(LumenSettings.audioKey) private var audioOn = true
    @AppStorage(LumenSettings.touchModeKey) private var touchMode = TouchEngine.Mode.touchscreen.rawValue
    @State private var recents: [RecentHost] = []
    @State private var showSettings = false

    var body: some View {
        NavigationStack {
            List {
                Section("Computers on your network") {
                    if client.hosts.isEmpty {
                        HStack { ProgressView(); Text("Searching…").foregroundStyle(.secondary) }
                    }
                    ForEach(client.hosts) { h in
                        Button {
                            client.connect(host: h, hello: makeHello())
                        } label: {
                            HStack {
                                Text(h.name)
                                Spacer()
                                if !h.platformLabel.isEmpty {
                                    Text(h.platformLabel).font(.footnote).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                }

                if !recents.isEmpty {
                    Section("Recent") {
                        ForEach(recents) { r in
                            Button {
                                client.connect(recent: r, hello: makeHello())
                            } label: {
                                HStack {
                                    Text(r.name)
                                    Spacer()
                                    Text(r.address).font(.footnote).foregroundStyle(.secondary)
                                }
                            }
                        }
                        .onDelete { offsets in
                            for i in offsets { RecentHosts.remove(address: recents[i].address) }
                            recents = RecentHosts.load()
                        }
                    }
                }

                Section("Cable") {
                    Label("Plug this device into your computer with a USB cable and start Lumen Host. It connects automatically.",
                          systemImage: "cable.connector")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                    if client.cablePaused {
                        Button("Resume cable auto-connect") { client.resumeCable() }
                    }
                }

                Section("Connect by IP address") {
                    TextField("e.g. 192.168.1.20", text: $manualHost)
                        .keyboardType(.numbersAndPunctuation)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    Button("Connect") { client.connect(host: manualHost, hello: makeHello()) }
                        .disabled(manualHost.trimmingCharacters(in: .whitespaces).isEmpty)
                }

                Section("Display") {
                    Picker("Display mode", selection: $extendMode) {
                        Text("Mirror").tag(false)
                        Text("Extend").tag(true)
                    }
                    .pickerStyle(.segmented)
                    Text(extendMode ? "Adds a new screen to the computer, sized for this device."
                                    : "Shows the computer's existing screen.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                    Toggle("Sound", isOn: $audioOn)
                }

                Section("Touch") {
                    Picker("Touch mode", selection: $touchMode) {
                        Text("Touchpad").tag(TouchEngine.Mode.touchpad.rawValue)
                        Text("Touchscreen").tag(TouchEngine.Mode.touchscreen.rawValue)
                    }
                    .pickerStyle(.segmented)
                }

                Section("Resolution") { ResolutionPicker() }

                Section("Quality") {
                    Picker("Frame rate", selection: $fps) {
                        Text("30").tag(30); Text("60").tag(60); Text("120").tag(120)
                    }
                    .pickerStyle(.segmented)
                    VStack(alignment: .leading) {
                        Text("Cable bitrate: \(Int(mbpsCable)) Mbps")
                        Slider(value: $mbpsCable, in: 8...150, step: 1)
                    }
                    VStack(alignment: .leading) {
                        Text("Wi-Fi bitrate: \(Int(mbpsWifi)) Mbps")
                        Slider(value: $mbpsWifi, in: 8...80, step: 1)
                    }
                }

                switch client.state {
                case .connecting: Section { HStack { ProgressView(); Text("Connecting…") } }
                case .failed(let m): Section { Text(m).foregroundStyle(.red) }
                default: EmptyView()
                }
            }
            .navigationTitle("Lumen")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { showSettings = true } label: { Image(systemName: "gearshape") }
                        .accessibilityLabel("Settings")
                }
            }
            .sheet(isPresented: $showSettings) { SettingsView() }
            .refreshable { await client.rescanAndWait() }
            .onAppear {
                recents = RecentHosts.load()
                client.setDiscoveryActive(true)
            }
            .onDisappear { client.setDiscoveryActive(false) }
        }
    }
}

// MARK: - Settings

struct SettingsView: View {
    @Environment(\.dismiss) private var dismiss
    @AppStorage(LumenSettings.touchModeKey) private var touchMode = TouchEngine.Mode.touchscreen.rawValue
    @AppStorage(LumenSettings.audioKey) private var audioOn = true
    @AppStorage(LumenSettings.extendKey) private var extendMode = false
    @AppStorage("controllersOn") private var controllersOn = true
    @AppStorage("keyboardAuto") private var keyboardAuto = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Picker("Touch mode", selection: $touchMode) {
                        Text("Touchpad").tag(TouchEngine.Mode.touchpad.rawValue)
                        Text("Touchscreen").tag(TouchEngine.Mode.touchscreen.rawValue)
                    }
                    .pickerStyle(.segmented)
                } header: { Text("Touch") } footer: {
                    Text("Touchpad moves the cursor like a laptop trackpad. Touchscreen sends your touches to the computer where you tap (native multi-touch on Windows and Linux).")
                }
                Section("Display") {
                    Picker("Display mode", selection: $extendMode) {
                        Text("Mirror").tag(false)
                        Text("Extend").tag(true)
                    }
                    .pickerStyle(.segmented)
                    Toggle("Sound", isOn: $audioOn)
                }
                Section("Resolution") { ResolutionPicker() }
                Section {
                    Toggle("Show the on-screen keyboard when streaming starts", isOn: $keyboardAuto)
                } header: { Text("Keyboard") } footer: {
                    Text("You can also open or close it any time from the ⋯ menu while streaming (Show keyboard). It types into the computer.")
                }
                Section {
                    Toggle("Send controllers to the computer", isOn: $controllersOn)
                } header: { Text("Controllers") } footer: {
                    Text("Connect an Xbox, PlayStation or MFi controller to this device over Bluetooth. It shows up as an Xbox 360 controller on the computer (Windows needs the ViGEmBus driver).")
                }
            }
            .navigationTitle("Settings")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        }
    }
}
