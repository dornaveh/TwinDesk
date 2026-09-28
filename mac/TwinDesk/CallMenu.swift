import Foundation
import AppKit

struct CallStatus: Codable {
    let updated: Date
    let cameraEnabled: Bool
    let automaticCameraEnabled: Bool?
    let microphoneEnabled: Bool
    let videoStatus: String
    let micStatus: String
    let speakerStatus: String
}
struct CallCommand: Codable {
    let id: UUID
    let created: Date
    let action: String
}
@MainActor final class CallMenu: ObservableObject {
    @Published var state: CallStatus?
    @Published var error = ""
    private var timer: Timer?
    private let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/TwinDesk Calls")
    init() {
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in Task { @MainActor in self?.refresh() } }
        launch(background: true)
    }
    private func refresh() {
        guard let data = try? Data(contentsOf: directory.appendingPathComponent("menu-status.json")), data.count < 16384,
              let value = try? JSONDecoder().decode(CallStatus.self, from: data),
              abs(value.updated.timeIntervalSinceNow) < 4 else { state = nil; return }
        state = value
    }
    func send(_ action: String) {
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let url = directory.appendingPathComponent("menu-command.json")
            let bytes = try JSONEncoder().encode(CallCommand(id: UUID(), created: Date(), action: action))
            try bytes.write(to: url, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            error = ""
            if state == nil { launch(background: true) }
        } catch { self.error = "Could not send the camera command: \(error.localizedDescription)" }
    }
    func launch(background: Bool) {
        let config = NSWorkspace.OpenConfiguration()
        config.activates = !background
        config.arguments = background ? ["--startup"] : []
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: "/Applications/TwinDesk-Calls.app"), configuration: config) { [weak self] _, error in
            if let error { Task { @MainActor in self?.error = "Calls helper: \(error.localizedDescription)" } }
        }
    }
}
