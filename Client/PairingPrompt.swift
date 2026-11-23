import UIKit

/// Asks the user for a host's pairing code with a text-field alert.
enum PairingPrompt {
    /// `completion` receives the parsed key, or nil when the user cancels. Must be called on the main thread.
    static func present(host: String, note: String?, completion: @escaping (Data?) -> Void) {
        guard let top = topViewController(), !(top is UIAlertController) else { completion(nil); return }
        var message = "Enter the 5-digit pairing code shown by Lumen Host on \(host)."
        if let note { message = note + "\n" + message }
        let alert = UIAlertController(title: "Pairing code", message: message, preferredStyle: .alert)
        alert.addTextField { tf in
            tf.placeholder = "12345"
            tf.autocapitalizationType = .none
            tf.autocorrectionType = .no
            tf.spellCheckingType = .no
            tf.keyboardType = .numberPad
            tf.clearButtonMode = .whileEditing
        }
        alert.addAction(UIAlertAction(title: "Cancel", style: .cancel) { _ in completion(nil) })
        alert.addAction(UIAlertAction(title: "Pair", style: .default) { [weak alert] _ in
            let text = alert?.textFields?.first?.text ?? ""
            if let key = PairingCode.parse(text) {
                completion(key)
            } else {
                // Let the dismissed alert finish leaving the screen before presenting the next one.
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
                    present(host: host, note: "That is not a valid pairing code.", completion: completion)
                }
            }
        })
        top.present(alert, animated: true)
    }

    private static func topViewController() -> UIViewController? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        let scene = scenes.first(where: { $0.activationState == .foregroundActive }) ?? scenes.first
        var vc = scene?.windows.first(where: { $0.isKeyWindow })?.rootViewController ?? scene?.windows.first?.rootViewController
        while let next = vc?.presentedViewController { vc = next }
        return vc
    }
}
