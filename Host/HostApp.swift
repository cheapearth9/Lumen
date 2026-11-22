import SwiftUI
import AppKit
import ApplicationServices

@main
struct LumenHostApp: App {
    @StateObject private var host = HostController()

    var body: some Scene {
        WindowGroup {
            HostView()
                .environmentObject(host)
                .frame(minWidth: 440, minHeight: 440)
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

    private let pipeline = StreamPipeline()
    private var timer: Timer?

    init() {
        refreshDisplays()
        refreshPermissions()
        pipeline.onStatus = { [weak self] s in Task { @MainActor in self?.status = s } }
        pipeline.onClient = { [weak self] c in
            Task { @MainActor in
                self?.clientConnected = c
                if !c { self?.stats = ""; if self?.isRunning == true { self?.status = "Waiting for a client…" } }
            }
        }
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
                Text("For an extra screen, create a virtual display (DeskPad or BetterDisplay) and pick it here.")
                    .font(.caption).foregroundStyle(.secondary)
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
