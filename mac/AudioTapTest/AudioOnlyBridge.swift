import Foundation

// Reuse TwinDesk's authenticated transport without any input or monitor control.
struct DisplayRoute {}
enum Displays {
    static func restore(_ routes: [DisplayRoute]) throws {
        throw BridgeError.message("Monitor control is unavailable in this audio-only test.")
    }
}
final class InputReceiver {
    func activate() throws { throw BridgeError.message("Audio test only. Use the main TwinDesk app for keyboard and mouse.") }
    func releaseAll() {}
    func handle(_ data: Data) throws {}
}
