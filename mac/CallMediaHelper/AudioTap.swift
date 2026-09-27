import Foundation
import CoreAudio

struct TapFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

final class AudioTap {
    private var tap: AudioObjectID = 0
    private var device: AudioObjectID = 0
    private var io: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "TwinDesk.core-audio")
    private lazy var outputVolume = OutputVolume(queue: queue)
    private var lastReport = 0.0
    var meter: ((Double) -> Void)?
    var send: ((Data) -> Void)?

    private func check(_ code: OSStatus, _ operation: String) throws {
        guard code == noErr else { throw TapFailure(message: "\(operation) failed (\(code)). Check System Audio Recording permission.") }
    }
    private func read<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, into value: inout T) throws {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<T>.size)
        try check(withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(object, &address, 0, nil, &size, $0) }, "Read audio property")
    }
    func start() throws -> String {
        guard tap == 0 else { return "Already running" }
        do {
            var output = AudioObjectID(0)
            try read(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultOutputDevice, into: &output)
            var outputUID: CFString = "" as CFString
            try read(output, kAudioDevicePropertyDeviceUID, into: &outputUID)
            guard outputUID as String != "local.twindesk.microphone" else {
                throw TapFailure(message: "Keep Mac speakers as the system output; TwinDesk Microphone is only for call input.")
            }
            outputVolume.start(device: output)
            var pid = getpid()
            var process = AudioObjectID(0)
            var size = UInt32(MemoryLayout<AudioObjectID>.size)
            var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
                mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                UInt32(MemoryLayout<pid_t>.size), &pid, &size, &process), "Identify call audio process")
            guard process != 0 else { throw TapFailure(message: "Cannot exclude microphone audio from speaker forwarding.") }
            let description = CATapDescription(excludingProcesses: [process], deviceUID: outputUID as String, stream: 0)
            description.name = "TwinDesk system audio"
            description.isPrivate = true
            description.muteBehavior = .mutedWhenTapped
            try check(AudioHardwareCreateProcessTap(description, &tap), "Create audio tap")
            var format = AudioStreamBasicDescription()
            try read(tap, kAudioTapPropertyFormat, into: &format)
            guard format.mFormatID == kAudioFormatLinearPCM,
                  format.mFormatFlags & kAudioFormatFlagIsFloat != 0,
                  format.mBitsPerChannel == 32, format.mChannelsPerFrame == 2,
                  format.mSampleRate == 48000 else {
                throw TapFailure(message: "TwinDesk currently requires a 48 kHz stereo output. The current audio format is unsupported.")
            }
            let config: [String: Any] = [
                kAudioAggregateDeviceNameKey: "TwinDesk Audio (temporary)",
                kAudioAggregateDeviceUIDKey: UUID().uuidString,
                kAudioAggregateDeviceIsPrivateKey: true,
                kAudioAggregateDeviceMainSubDeviceKey: outputUID,
                kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
                kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: description.uuid.uuidString, kAudioSubTapDriftCompensationKey: true]],
                kAudioAggregateDeviceTapAutoStartKey: true
            ]
            try check(AudioHardwareCreateAggregateDevice(config as CFDictionary, &device), "Create temporary audio device")
            lastReport = 0
            try check(AudioDeviceCreateIOProcIDWithBlock(&io, device, queue) { [weak self] _, input, _, _, _ in
                guard let self else { return }
                // Samples remain in memory; forwarding is gated by the paired connection.
                let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
                var peak: Float = 0
                for buffer in buffers {
                    guard let data = buffer.mData else { continue }
                    let samples = data.assumingMemoryBound(to: Float.self)
                    for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Float>.size) {
                        let sample = samples[index]
                        if sample.isFinite { peak = max(peak, abs(sample * self.outputVolume.gain)) }
                    }
                }
                if let data = TapPCM.encode(buffers, gain: self.outputVolume.gain) {
                    for offset in stride(from: 0, to: data.count, by: 1920) {
                        self.send?(data.subdata(in: offset..<min(offset + 1920, data.count)))
                    }
                }
                let now = ProcessInfo.processInfo.systemUptime
                if now - self.lastReport >= 0.1 {
                    self.lastReport = now
                    self.meter?(Double(peak))
                }
            }, "Create audio callback")
            try check(AudioDeviceStart(device, io), "Start audio-only capture")
            return "Capturing audio only · \(Int(format.mSampleRate)) Hz · \(format.mChannelsPerFrame) channels"
        } catch { stop(); throw error }
    }
    func stop() {
        if let io { AudioDeviceStop(device, io); AudioDeviceDestroyIOProcID(device, io) }
        io = nil
        if device != 0 { AudioHardwareDestroyAggregateDevice(device); device = 0 }
        if tap != 0 { AudioHardwareDestroyProcessTap(tap); tap = 0 }
        outputVolume.stop()
    }
    deinit { stop() }
}
