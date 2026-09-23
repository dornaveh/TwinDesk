import Foundation

// Audio-only Core Audio tap: no ScreenCaptureKit session or display capture.
final class AudioCapture {
    private let tap = AudioTap()
    var send: ((Data) -> Void)? {
        didSet { tap.send = send }
    }
    var report: ((String) -> Void)?
    @MainActor func start() async throws {
        _ = try tap.start()
        report?("Audio streaming to the PC. Mac speaker muted; no screen capture.")
    }
    @MainActor func stop() async {
        tap.stop()
        report?("System audio forwarding is stopped. Local Mac playback restored.")
    }
}
