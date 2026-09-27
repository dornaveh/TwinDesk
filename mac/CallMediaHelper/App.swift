import SwiftUI
import AppKit
import OSLog

// Separate from the trusted KVM app: no Keychain, input injection, screen
// capture. Speaker audio uses an audio-only tap; scoped credentials are stored privately.
@MainActor final class CallModel: ObservableObject {
    @Published var setup = ""
    @Published var videoStatus = "Camera stopped"
    @Published var micStatus = "Microphone stopped"
    @Published var speakerStatus = "Speaker audio stopped"
    @Published var running = false
    @Published var cameraEnabled = false
    @Published var microphoneEnabled = UserDefaults.standard.bool(forKey: "microphoneEnabled")
    private var activePairing: Pairing?
    private let logger = Logger(subsystem: "local.twindesk.calls", category: "media")
    private let video = CallMediaConnection(.camera)
    private let mic = CallMediaConnection(.microphone)
    private let speakers = CallMediaConnection(.speakers)
    private var safetyTimer: Timer?
    private let demandMonitor = MediaDemandMonitor()
    private var wakeObservers: [NSObjectProtocol] = []
    private let configURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/TwinDesk Calls/media-pairing.json")
    init() {
        video.report = { [weak self] message in DispatchQueue.main.async { self?.videoStatus = message; self?.logger.info("Camera: \(message, privacy: .public)") } }
        mic.report = { [weak self] message in DispatchQueue.main.async { self?.micStatus = message; self?.logger.info("Microphone: \(message, privacy: .public)") } }
        speakers.report = { [weak self] message in DispatchQueue.main.async { self?.speakerStatus = message; self?.logger.info("Speakers: \(message, privacy: .public)") } }
        if let data = try? Data(contentsOf: configURL), data.count < 8192 {
            setup = data.base64EncodedString()
            DispatchQueue.main.async { [weak self] in self?.start() }
        }
        safetyTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.processMenuCommand()
                self.publishMenuStatus()
                guard self.running else { return }
                self.updateDemand()
                guard !self.oldAudioIsOff else { return }
                self.stop()
                self.speakerStatus = "Speaker routing conflict detected. TwinDesk setup needs to complete the audio handoff."
            }
        }
        wakeObservers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.stop() }
        })
        wakeObservers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.start() }
        })
    }
    private var oldAudioIsOff: Bool {
        CFPreferencesAppSynchronize("local.twindesk.mac" as CFString)
        return (CFPreferencesCopyAppValue("sendAudio" as CFString, "local.twindesk.mac" as CFString) as? Bool) == false
    }
    private struct Setup: Codable { let pairingCode: String; let session: String? }
    func start() {
        do {
            guard oldAudioIsOff else {
                throw BridgeError.message("TwinDesk setup must complete the automatic speaker-audio handoff first.")
            }
            guard setup.utf8.count < 8192, let data = Data(base64Encoded: setup.trimmingCharacters(in: .whitespacesAndNewlines)) else {
                throw BridgeError.message("Enter the call-media setup code supplied by Windows TwinDesk.")
            }
            let config = try JSONDecoder().decode(Setup.self, from: data)
            let pairing = try Pairing.parse(config.pairingCode)
            let session = config.session ?? ""
            guard session.isEmpty || (session.count == 32 && session.allSatisfy({ $0.isHexDigit })) else {
                throw BridgeError.message("Invalid call-media session.")
            }
            let directory = configURL.deletingLastPathComponent()
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
            try data.write(to: configURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: configURL.path)
            speakers.start(pairing, session: session)
            activePairing = pairing
            running = true
            mediaChanged()
            setup = ""
        } catch { videoStatus = error.localizedDescription; logger.error("Setup failed: \(error.localizedDescription, privacy: .public)") }
    }
    func stop() {
        cameraEnabled = false
        video.setDemand(false); mic.setDemand(false)
        video.stop(); mic.stop(); speakers.stop(); running = false
        activePairing = nil
        if let data = try? Data(contentsOf: configURL), data.count < 8192 { setup = data.base64EncodedString() }
    }
    private func updateDemand() {
        video.setDemand(cameraEnabled)
        mic.setDemand(microphoneEnabled && demandMonitor.microphoneInUse())
    }
    func toggleCamera() {
        guard running else { return }
        cameraEnabled.toggle()
        mediaChanged()
    }
    func mediaChanged() {
        UserDefaults.standard.set(microphoneEnabled, forKey: "microphoneEnabled")
        guard running, let pairing = activePairing else { return }
        updateDemand()
        if cameraEnabled { video.start(pairing, session: "") } else { video.stop() }
        if microphoneEnabled { mic.start(pairing, session: "") } else { mic.stop() }
    }
}

@main struct CallMediaApp: App {
    @StateObject private var model = CallModel()
    @Environment(\.openWindow) private var openWindow
    var body: some Scene {
        WindowGroup("TwinDesk Calls", id: "calls") {
            VStack(alignment: .leading, spacing: 14) {
                Text("PC webcam and microphone").font(.title2)
                Text("Mac speaker audio flows automatically to the PC. Start and stop the camera feed here. The microphone activates only while a Mac app uses BlackHole 2ch.")
                SecureField("Call-media setup code from Windows", text: $model.setup).disabled(model.running)
                HStack {
                    Button("Start", action: model.start).disabled(model.running || model.setup.isEmpty)
                }
                Button(model.cameraEnabled ? "Stop camera feed" : "Start camera feed", action: model.toggleCamera).disabled(!model.running)
                Toggle("Allow PC microphone when an app uses it", isOn: $model.microphoneEnabled).onChange(of: model.microphoneEnabled) { model.mediaChanged() }
                Text(model.videoStatus)
                Text(model.micStatus)
                Text(model.speakerStatus)
                Text("In your calling app, select OBS Virtual Camera and BlackHole 2ch. Keep its speakers on the normal Mac output.").font(.callout)
            }.padding(24).frame(width: 480)
                .onAppear { if ProcessInfo.processInfo.arguments.contains("--startup") { NSApp.hide(nil) } }
        }.windowResizability(.contentSize)
    }
}
