@main enum CameraDemandPolicyTests {
    static func main() {
        var policy = CameraDemandPolicy(automatic: true)
        precondition(!policy.cameraWanted(microphoneAllowed: true, microphoneInUse: false))
        precondition(policy.cameraWanted(microphoneAllowed: true, microphoneInUse: true))
        precondition(!policy.cameraWanted(microphoneAllowed: false, microphoneInUse: true))
        precondition(!policy.microphoneWanted(microphoneAllowed: true, microphoneInUse: false))
        policy.setManual(false) // Stop overrides an active automatic call.
        precondition(!policy.cameraWanted(microphoneAllowed: true, microphoneInUse: true))
        policy.setManual(true) // Camera-only preview can still be started explicitly.
        precondition(policy.cameraWanted(microphoneAllowed: false, microphoneInUse: false))
        precondition(policy.microphoneWanted(microphoneAllowed: true, microphoneInUse: false))
        policy.suspend() // Manual capture does not resume on wake.
        precondition(!policy.cameraWanted(microphoneAllowed: true, microphoneInUse: false))
        policy.setAutomatic(true)
        precondition(!policy.cameraWanted(microphoneAllowed: true, microphoneInUse: false))
        precondition(policy.cameraWanted(microphoneAllowed: true, microphoneInUse: true))
        policy.suspend()
        precondition(policy.automatic)
        print("Camera demand policy tests passed.")
    }
}
