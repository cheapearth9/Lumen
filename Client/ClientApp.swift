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
        let config = UISceneConfiguration(name: nil, sessionRole: connectingSceneSession.role)
        if connectingSceneSession.role == .windowExternalDisplayNonInteractive {
            config.delegateClass = ExternalSceneDelegate.self
        }
        return config
    }
}

final class ExternalSceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }
        let vc = ExternalVideoController()
        let w = UIWindow(windowScene: windowScene)
        w.rootViewController = vc
        w.isHidden = false
        window = w
    }
}

final class ExternalVideoController: UIViewController {
    override func loadView() { view = VideoView() }
    override var prefersStatusBarHidden: Bool { true }
}

// MARK: - Root UI

struct RootView: View {
    @StateObject private var client = StreamClient()
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage("fps") private var fps = 60
    @AppStorage("mbps") private var mbps = 40.0
    @AppStorage("manualHost") private var manualHost = ""

    var body: some View {
        Group {
            if case .streaming = client.state {
                StreamControllerView(client: client)
                    .ignoresSafeArea()
                    .statusBarHidden(true)
                    .persistentSystemOverlays(.hidden)
            } else {
                ConnectView(client: client, fps: $fps, mbps: $mbps, manualHost: $manualHost, makeHello: makeHello)
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

    /// Ask the Mac for the native resolution of whichever screen will show the video.
    private func makeHello() -> HelloInfo {
        let screens = UIScreen.screens
        let target = screens.count > 1 ? screens[screens.count - 1] : UIScreen.main
        let b = target.nativeBounds
        let w = min(3840, max(b.width, b.height))
        let h = min(2160, min(b.width, b.height))
        return HelloInfo(width: UInt16(w), height: UInt16(h), fps: UInt8(fps), bitrateMbps: UInt16(mbps),
                         flags: LumenSettings.helloFlags())
    }
}

struct ConnectView: View {
    @ObservedObject var client: StreamClient
    @Binding var fps: Int
    @Binding var mbps: Double
    @Binding var manualHost: String
    let makeHello: () -> HelloInfo

    @AppStorage(LumenSettings.extendKey) private var extendMode = false
    @AppStorage(LumenSettings.audioKey) private var audioOn = true
    @AppStorage(LumenSettings.touchModeKey) private var touchMode = TouchEngine.Mode.touchscreen.rawValue
    @State private var recents: [RecentHost] = []

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

                Section("Quality") {
                    Picker("Frame rate", selection: $fps) {
                        Text("30").tag(30); Text("60").tag(60); Text("120").tag(120)
                    }
                    .pickerStyle(.segmented)
                    VStack(alignment: .leading) {
                        Text("Bitrate: \(Int(mbps)) Mbps")
                        Slider(value: $mbps, in: 8...150, step: 1)
                    }
                }

                switch client.state {
                case .connecting: Section { HStack { ProgressView(); Text("Connecting…") } }
                case .failed(let m): Section { Text(m).foregroundStyle(.red) }
                default: EmptyView()
                }
            }
            .navigationTitle("Lumen")
            .refreshable { await client.rescanAndWait() }
            .onAppear {
                recents = RecentHosts.load()
                client.setDiscoveryActive(true)
            }
            .onDisappear { client.setDiscoveryActive(false) }
        }
    }
}
