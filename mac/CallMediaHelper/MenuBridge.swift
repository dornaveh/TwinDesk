import Foundation
import AppKit

private struct CallCommand: Codable {
    let id: UUID
    let created: Date
    let action: String
}
private struct CallStatus: Codable {
    let updated: Date
    let cameraEnabled: Bool
    let microphoneEnabled: Bool
    let videoStatus: String
    let micStatus: String
    let speakerStatus: String
}
extension CallModel {
    private var menuDirectory: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/TwinDesk Calls") }
    func processMenuCommand() {
        // Only narrow media controls; never accepts paths, credentials or input events.
        let url = menuDirectory.appendingPathComponent("menu-command.json")
        guard running, let bytes = try? Data(contentsOf: url) else { return }
        try? FileManager.default.removeItem(at: url)
        guard bytes.count < 2048, let command = try? JSONDecoder().decode(CallCommand.self, from: bytes),
              abs(command.created.timeIntervalSinceNow) < 10 else { return }
        switch command.action {
        case "startCamera": cameraEnabled = true
        case "stopCamera": cameraEnabled = false
        case "enableMicrophone": microphoneEnabled = true
        case "disableMicrophone": microphoneEnabled = false
        case "quitHelper":
            stop(); publishMenuStatus()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { NSApp.terminate(nil) }
            return
        default: return
        }
        mediaChanged()
    }
    func publishMenuStatus() {
        let status = CallStatus(updated: Date(), cameraEnabled: cameraEnabled, microphoneEnabled: microphoneEnabled,
            videoStatus: videoStatus, micStatus: micStatus, speakerStatus: speakerStatus)
        let url = menuDirectory.appendingPathComponent("menu-status.json")
        if let bytes = try? JSONEncoder().encode(status) {
            try? bytes.write(to: url, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }
    }
}
