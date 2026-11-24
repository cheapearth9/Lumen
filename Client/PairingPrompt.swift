import UIKit

/// Shows the 5-digit pairing code on the device. The user types it on the computer; nothing is entered here.
enum PairingPrompt {
    private static var current: UIAlertController?
    private static var wanted = false

    /// Shows the code with a Cancel button. Must be called on the main thread.
    static func show(host: String, code: String, onCancel: @escaping () -> Void) {
        dismiss()
        wanted = true
        present(host: host, code: code, onCancel: onCancel, attempts: 6)
    }

    /// Removes the prompt (if any). Must be called on the main thread.
    static func dismiss() {
        wanted = false
        if let a = current { a.dismiss(animated: true) }
        current = nil
    }

    /// A simple "OK" alert for a failure such as "Wrong code. Try pairing again."
    static func showFailure(_ text: String) {
        guard let top = topViewController(), !(top is UIAlertController) else { return }
        let alert = UIAlertController(title: "Pairing", message: text, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "OK", style: .default))
        top.present(alert, animated: true)
    }

    private static func present(host: String, code: String, onCancel: @escaping () -> Void, attempts: Int) {
        guard wanted else { return }
        guard let top = topViewController(), !(top is UIAlertController) else {
            if attempts > 0 {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    present(host: host, code: code, onCancel: onCancel, attempts: attempts - 1)
                }
            }
            return
        }
        let alert = UIAlertController(title: "Pair with \(host)", message: nil, preferredStyle: .alert)
        let text = NSMutableAttributedString(string: "On \(host), type this code when asked:\n\n",
                                             attributes: [.font: UIFont.systemFont(ofSize: 13), .foregroundColor: UIColor.label])
        text.append(NSAttributedString(string: code,
                                       attributes: [.font: UIFont.monospacedSystemFont(ofSize: 44, weight: .bold),
                                                    .foregroundColor: UIColor.label]))
        alert.setValue(text, forKey: "attributedMessage")
        alert.addAction(UIAlertAction(title: "Cancel", style: .cancel) { _ in
            current = nil
            wanted = false
            onCancel()
        })
        current = alert
        top.present(alert, animated: true) {
            if !wanted && current === alert { alert.dismiss(animated: false); current = nil }
        }
    }

    private static func topViewController() -> UIViewController? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        let scene = scenes.first(where: { $0.activationState == .foregroundActive }) ?? scenes.first
        var vc = scene?.windows.first(where: { $0.isKeyWindow })?.rootViewController ?? scene?.windows.first?.rootViewController
        while let next = vc?.presentedViewController { vc = next }
        return vc
    }
}
