import Foundation

/// User preferences shared by the SwiftUI connect screen (via @AppStorage with the same keys) and UIKit code.
enum LumenSettings {
    static let audioKey = "audioOn"
    static let extendKey = "extendMode"
    static let touchModeKey = "touchMode"

    /// Sound on by default.
    static var audioOn: Bool {
        get { UserDefaults.standard.object(forKey: audioKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: audioKey) }
    }

    /// false = Mirror, true = Extend.
    static var extend: Bool {
        get { UserDefaults.standard.bool(forKey: extendKey) }
        set { UserDefaults.standard.set(newValue, forKey: extendKey) }
    }

    static var touchMode: TouchEngine.Mode {
        get { TouchEngine.Mode(rawValue: UserDefaults.standard.string(forKey: touchModeKey) ?? "") ?? .touchscreen }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: touchModeKey) }
    }

    /// Hello flags for the current preferences.
    static func helloFlags() -> UInt8 {
        var f: UInt8 = 0
        if audioOn { f |= HelloInfo.flagAudio }
        if extend { f |= HelloInfo.flagExtend }
        return f
    }
}
