import Foundation
import CoreAudio
import AudioToolbox

// Write only to BlackHole's output; calling apps select its corresponding input.
// This never changes the Mac's default input/output device and never monitors the mic.
final class MicrophoneOutput {
    private var unit: AudioUnit?
    private let lock = NSLock()
    private var ring = [Float](repeating: 0, count: 3840) // 40 ms stereo, oldest frames dropped
    private var readIndex = 0
    private var count = 0

    private func check(_ status: OSStatus, _ operation: String) throws {
        if status != noErr { throw BridgeError.message("\(operation) failed (\(status)). Check BlackHole 2ch installation.") }
    }
    private func findDevice() throws -> AudioDeviceID {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        try check(AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size), "Read audio devices")
        guard size <= 16384, size % 4 == 0 else { throw BridgeError.message("Invalid audio device list.") }
        var devices = [AudioDeviceID](repeating: 0, count: Int(size / 4))
        try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &devices), "Read audio devices")
        for device in devices {
            address.mSelector = kAudioDevicePropertyDeviceUID
            var uid: Unmanaged<CFString>?; size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
            if AudioObjectGetPropertyData(device, &address, 0, nil, &size, &uid) == noErr, let uid, uid.takeRetainedValue() as String == "BlackHole2ch_UID" {
                var defaultDevice: AudioDeviceID = 0
                address.mSelector = kAudioHardwarePropertyDefaultOutputDevice; size = 4
                try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &defaultDevice), "Read default output")
                guard defaultDevice != device else { throw BridgeError.message("Keep Mac speakers as the system output. Select BlackHole 2ch only as the microphone in your calling app.") }
                return device
            }
        }
        throw BridgeError.message("Install BlackHole 2ch to use the PC microphone in Mac calls.")
    }
    func start() throws {
        guard unit == nil else { return }
        var device = try findDevice()
        var description = AudioComponentDescription(componentType: kAudioUnitType_Output, componentSubType: kAudioUnitSubType_HALOutput, componentManufacturer: kAudioUnitManufacturer_Apple, componentFlags: 0, componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &description) else { throw BridgeError.message("Audio output component unavailable.") }
        var instance: AudioUnit?
        try check(AudioComponentInstanceNew(component, &instance), "Create microphone output")
        guard let instance else { throw BridgeError.message("Cannot create microphone output.") }
        unit = instance
        do {
            try check(AudioUnitSetProperty(instance, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &device, 4), "Select BlackHole")
            var format = AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM, mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8, mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
            try check(AudioUnitSetProperty(instance, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, &format, UInt32(MemoryLayout<AudioStreamBasicDescription>.size)), "Set microphone format")
            var callback = AURenderCallbackStruct(inputProc: { ref, _, _, _, _, data in
                guard let data else { return noErr }
                let output = Unmanaged<MicrophoneOutput>.fromOpaque(ref).takeUnretainedValue()
                output.render(data)
                return noErr
            }, inputProcRefCon: Unmanaged.passUnretained(self).toOpaque())
            try check(AudioUnitSetProperty(instance, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0, &callback, UInt32(MemoryLayout<AURenderCallbackStruct>.size)), "Set microphone callback")
            try check(AudioUnitInitialize(instance), "Initialize microphone output")
            try check(AudioOutputUnitStart(instance), "Start microphone output")
        } catch { stop(); throw error }
    }
    func receive(_ data: Data) throws {
        guard !data.isEmpty, data.count <= 19200, data.count % 4 == 0 else { throw BridgeError.message("Invalid microphone audio packet.") }
        try start()
        lock.lock(); defer { lock.unlock() }
        // Keep the newest 40 ms even when packets arrive in bursts.
        let bytes = data.suffix(ring.count * 2)
        let samples = bytes.count / 2
        let overflow = max(0, count + samples - ring.count)
        readIndex = (readIndex + overflow) % ring.count; count -= overflow
        bytes.withUnsafeBytes { raw in
            for i in 0..<samples {
                let value = Int16(littleEndian: raw.loadUnaligned(fromByteOffset: i * 2, as: Int16.self))
                ring[(readIndex + count) % ring.count] = Float(value) / 32768
                count += 1
            }
        }
    }
    private func render(_ list: UnsafeMutablePointer<AudioBufferList>) {
        // No allocation or blocking on the audio callback; contention yields silence.
        let buffers = UnsafeMutableAudioBufferListPointer(list)
        for buffer in buffers { if let base = buffer.mData { memset(base, 0, Int(buffer.mDataByteSize)) } }
        guard lock.try() else { return }; defer { lock.unlock() }
        guard buffers.count == 1, buffers[0].mNumberChannels == 2, let base = buffers[0].mData else { return }
        let output = base.assumingMemoryBound(to: Float.self)
        let length = min(Int(buffers[0].mDataByteSize) / 4, count) / 2 * 2
        for i in 0..<length { output[i] = ring[(readIndex + i) % ring.count] }
        readIndex = (readIndex + length) % ring.count; count -= length
    }
    func stop() {
        if let unit { AudioOutputUnitStop(unit); AudioUnitUninitialize(unit); AudioComponentInstanceDispose(unit) }
        unit = nil
        lock.lock(); count = 0; readIndex = 0; lock.unlock()
    }
    deinit { stop() }
}
