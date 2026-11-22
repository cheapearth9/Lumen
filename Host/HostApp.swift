import SwiftUI
import AppKit
import ApplicationServices
import Darwin

@main
struct LumenHostApp: App {
    @StateObject private var host = HostController()

    var body: some Scene {
        WindowGroup {
            HostView()
                .environmentObject(host)
                .frame(minWidth: 460, minHeight: 640)
        }
        .windowResizability(.contentSize)
    }
}

@MainActor
final class HostController: ObservableObject {
    struct DisplayInfo: Identifiable, Hashable {
        let id: CGDirectDisplayID
        let name: String
        let width: Int
        let height: Int
    }

    @Published var displays: [DisplayInfo] = []
    @Published var selectedDisplay: CGDirectDisplayID = CGMainDisplayID()
    @Published var isRunning = false
    @Published var clientConnected = false
    @Published var status = "Stopped"
    @Published var stats = ""
    @Published var screenAllowed = false
    @Published var accessibilityAllowed = false
    @Published var extended = false
    @Published var pairingCode = ""
    @Published var addresses: [String] = []

    private let pipeline = StreamPipeline()
    private var timer: Timer?

    init() {
        refreshDisplays()
        refreshPermissions()
        pairingCode = PairingCode.format(pipeline.pairingKey)
        addresses = Self.localAddresses()
        pipeline.onMode = { [weak self] e in Task { @MainActor in self?.extended = e } }
        pipeline.onStatus = { [weak self] s in Task { @MainActor in self?.status = s } }
        pipeline.onClient = { [weak self] c in
            Task { @MainActor in
                self?.clientConnected = c
                if !c { self?.stats = ""; if self?.isRunning == true { self?.status = "Waiting for a client…" } }
            }
        }
    }

    func setMode(extend: Bool) {
        extended = extend
        pipeline.setMode(extend: extend)
    }

    func newCode() { pairingCode = PairingCode.format(pipeline.newPairingKey()) }

    func refreshAddresses() { addresses = Self.localAddresses() }

    /// IPv4 addresses of this Mac ("192.168.1.20  (en0)").
    static func localAddresses() -> [String] {
        var result = [String]()
        var ifap: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifap) == 0, let first = ifap else { return [] }
        defer { freeifaddrs(ifap) }
        var p: UnsafeMutablePointer<ifaddrs>? = first
        while let cur = p {
            let ifa = cur.pointee
            if let sa = ifa.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET),
               Int32(ifa.ifa_flags) & IFF_LOOPBACK == 0, Int32(ifa.ifa_flags) & IFF_UP != 0 {
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                    let name = String(cString: ifa.ifa_name)
                    if !name.hasPrefix("utun") && !name.hasPrefix("awdl") && !name.hasPrefix("llw") && !name.hasPrefix("bridge") {
                        result.append("\(String(cString: host))  (\(name))")
                    }
                }
            }
            p = ifa.ifa_next
        }
        return result
    }

    func refreshDisplays() {
        displays = NSScreen.screens.compactMap { s in
            guard let n = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return nil }
            let id = CGDirectDisplayID(n.uint32Value)
            return DisplayInfo(id: id, name: s.localizedName,
                               width: CGDisplayPixelsWide(id), height: CGDisplayPixelsHigh(id))
        }
        if !displays.contains(where: { $0.id == selectedDisplay }), let first = displays.first {
            selectedDisplay = first.id
        }
    }

    func refreshPermissions() {
        screenAllowed = CGPreflightScreenCaptureAccess()
        accessibilityAllowed = AXIsProcessTrusted()
    }

    func requestScreen() { _ = CGRequestScreenCaptureAccess(); refreshPermissions() }

    func requestAccessibility() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
        refreshPermissions()
    }

    func start() {
        pipeline.displayID = selectedDisplay
        do {
            try pipeline.start(name: Host.current().localizedName ?? "Mac")
            isRunning = true
            status = "Waiting for a client…"
            timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
                Task { @MainActor in
                    guard let self else { return }
                    let s = self.pipeline.drainStats()
                    if self.clientConnected {
                        self.stats = String(format: "%.1f Mbps · %d fps", Double(s.bytes) * 8 / 1e6, s.frames)
                    }
                }
            }
        } catch {
            status = "Could not start: \(error.localizedDescription)"
        }
    }

    func stop() {
        timer?.invalidate(); timer = nil
        pipeline.stop()
        isRunning = false
        clientConnected = false
        stats = ""
        status = "Stopped"
    }
}

struct HostView: View {
    @EnvironmentObject var host: HostController

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Lumen Host").font(.largeTitle.bold())

            GroupBox("Your IP address") {
                VStack(alignment: .leading, spacing: 4) {
                    if host.addresses.isEmpty { Text("No network connection found") }
                    ForEach(host.addresses, id: \.self) { a in Text(a).font(.system(.body, design: .monospaced)).textSelection(.enabled) }
                    HStack {
                        Button("Copy") {
                            if let first = host.addresses.first?.split(separator: " ").first {
                                NSPasteboard.general.clearContents()
                                NSPasteboard.general.setString(String(first), forType: .string)
                            }
                        }
                        Button("Refresh") { host.refreshAddresses() }
                    }
                    Text("Type this into the iPad app if the Mac is not listed.").font(.caption).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading).padding(4)
            }

            GroupBox("Pairing code (encryption)") {
                HStack {
                    Text(host.pairingCode).font(.system(.title3, design: .monospaced)).textSelection(.enabled)
                    Spacer()
                    Button("New code") { host.newCode() }
                }
                .padding(4)
                Text("Enter this code once in the app. Everything streamed is encrypted with it.").font(.caption).foregroundStyle(.secondary)
            }

            GroupBox("Screen mode") {
                VStack(alignment: .leading, spacing: 6) {
                    Picker("", selection: Binding(get: { host.extended }, set: { host.setMode(extend: $0) })) {
                        Text("Mirror").tag(false)
                        Text("Extend").tag(true)
                    }
                    .pickerStyle(.segmented).labelsHidden()
                    Text(host.extended ? "Extend: the iPad is an additional screen (virtual display)." : "Mirror: the iPad shows the display chosen below.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .padding(4)
            }

            GroupBox("Display to stream") {
                HStack {
                    Picker("", selection: $host.selectedDisplay) {
                        ForEach(host.displays) { d in
                            Text("\(d.name) (\(d.width)×\(d.height))").tag(d.id)
                        }
                    }
                    .labelsHidden()
                    .disabled(host.isRunning)
                    Button("Refresh") { host.refreshDisplays() }.disabled(host.isRunning)
                }
                .padding(4)
            }

            GroupBox("Permissions") {
                VStack(alignment: .leading, spacing: 8) {
                    permissionRow("Screen Recording (capture video)", ok: host.screenAllowed, action: host.requestScreen)
                    permissionRow("Accessibility (mouse & keyboard)", ok: host.accessibilityAllowed, action: host.requestAccessibility)
                    Button("Re-check") { host.refreshPermissions() }
                    Text("After granting, quit and reopen the app once.").font(.caption).foregroundStyle(.secondary)
                }
                .padding(4)
            }

            HStack {
                Button(host.isRunning ? "Stop" : "Start") { host.isRunning ? host.stop() : host.start() }
                    .keyboardShortcut(.defaultAction)
                    .controlSize(.large)
                Circle().fill(host.clientConnected ? .green : (host.isRunning ? .orange : .gray)).frame(width: 10, height: 10)
                VStack(alignment: .leading) {
                    Text(host.status)
                    if !host.stats.isEmpty { Text(host.stats).font(.caption).foregroundStyle(.secondary) }
                }
            }
            Spacer()
        }
        .padding(20)
    }

    @ViewBuilder
    private func permissionRow(_ title: String, ok: Bool, action: @escaping () -> Void) -> some View {
        HStack {
            Image(systemName: ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(ok ? .green : .orange)
            Text(title)
            Spacer()
            if !ok { Button("Allow", action: action) }
        }
    }
}
