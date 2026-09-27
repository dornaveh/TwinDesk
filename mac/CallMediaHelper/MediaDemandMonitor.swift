import Foundation
import CoreAudio

// Read-only device usage queries. Never opens a capture session or requests camera/mic access.
final class MediaDemandMonitor {
    private func values(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector,
                        scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> [UInt32] {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(object, &address, 0, nil, &size) == noErr,
              size > 0, size <= 65536, size % 4 == 0 else { return [] }
        var result = [UInt32](repeating: 0, count: Int(size / 4))
        guard AudioObjectGetPropertyData(object, &address, 0, nil, &size, &result) == noErr else { return [] }
        return Array(result.prefix(Int(size / 4)))
    }
    private func isTwinDeskMicrophone(_ device: AudioObjectID) -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var uid: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &uid) == noErr, let uid else { return false }
        return uid.takeRetainedValue() as String == "local.twindesk.microphone"
    }
    func microphoneInUse() -> Bool {
        for process in values(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyProcessObjectList) {
            guard values(process, kAudioProcessPropertyPID).first != UInt32(getpid()),
                  values(process, kAudioProcessPropertyIsRunningInput).first == 1 else { continue }
            if values(process, kAudioProcessPropertyDevices, scope: kAudioObjectPropertyScopeInput).contains(where: isTwinDeskMicrophone) { return true }
        }
        return false
    }
}
