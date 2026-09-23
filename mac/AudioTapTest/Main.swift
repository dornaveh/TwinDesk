import SwiftUI
import CoreAudio

@MainActor final class TestModel: ObservableObject {
    @Published var running = false
    @Published var status = "Ready. Keep TwinDesk audio forwarding off during this test."
    @Published var peak = 0.0
    @Published var receivedSound = false
    @Published var forwarding = false
    @Published var linkStatus = "Not sending to PC. Keep the main TwinDesk app disconnected."
    private let capture = AudioTap()
    private let connection = Connection()
    init() {
        let link = connection
        capture.send = { link.sendAudio($0) }
        connection.report = { [weak self] text in DispatchQueue.main.async { self?.linkStatus = text } }
        capture.meter = { [weak self] value in
            DispatchQueue.main.async {
                guard let self, self.running else { return }
                self.peak = value
                if value > 0.0001 { self.receivedSound = true }
            }
        }
    }
    func start() {
        receivedSound = false
        do { status = try capture.start(); running = true }
        catch { status = error.localizedDescription }
    }
    func forward() {
        do {
            let pairing = try Pairing.parse(PairingStore.load())
            connection.start(pairing, routes: [])
            forwarding = true
        } catch { linkStatus = "Cannot load saved pairing: " + error.localizedDescription }
    }
    func stop() { capture.stop(); connection.stop(); forwarding = false; running = false; peak = 0; status = "Stopped. Temporary audio tap removed." }
}

@main struct AudioTapTestApp: App {
    @StateObject private var model = TestModel()
    var body: some Scene {
        WindowGroup("TwinDesk Audio Test") {
            VStack(alignment: .leading, spacing: 18) {
                Text("Apple TV audio-only test").font(.title)
                Text("Audio-only capture. No screen capture or audio files. Local Mac playback is muted while the test runs; stopping restores it.")
                Text(model.status).textSelection(.enabled)
                ProgressView(value: min(model.peak, 1))
                Text(model.receivedSound ? "Sound received" : "No sound detected yet")
                Text("Peak: \(model.peak, specifier: "%.5f")").monospacedDigit()
                HStack {
                    Button("Start audio test") { model.start() }.disabled(model.running)
                    Button("Stop test") { model.stop() }.disabled(!model.running)
                }
                Text(model.linkStatus).textSelection(.enabled)
                Button("Send audio to PC") { model.forward() }.disabled(!model.running || model.forwarding)
                Text("Play Apple TV. Check that video stays visible and the meter moves. Pause playback to check that its audio level falls.").foregroundStyle(.secondary)
            }.padding(24).frame(width: 520)
                .onReceive(NotificationCenter.default.publisher(for: NSApplication.willTerminateNotification)) { _ in model.stop() }
                .onDisappear { model.stop() }
        }
    }
}
