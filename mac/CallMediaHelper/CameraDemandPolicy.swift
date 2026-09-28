// Automatic camera demand follows external microphone use, never the fact
// that TwinDesk itself is sending video or receiving PC microphone packets.
struct CameraDemandPolicy {
    var automatic: Bool
    private(set) var manual = false

    mutating func setAutomatic(_ enabled: Bool) {
        automatic = enabled
        manual = false
    }
    mutating func setManual(_ enabled: Bool) {
        automatic = false
        manual = enabled
    }
    mutating func suspend() { manual = false }
    func cameraWanted(microphoneAllowed: Bool, microphoneInUse: Bool) -> Bool {
        manual || (automatic && microphoneAllowed && microphoneInUse)
    }
    func microphoneWanted(microphoneAllowed: Bool, microphoneInUse: Bool) -> Bool {
        microphoneAllowed && (manual || microphoneInUse)
    }
}
