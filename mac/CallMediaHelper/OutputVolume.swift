import Foundation
import CoreAudio
import AudioToolbox

final class OutputVolume {
    private let queue: DispatchQueue
    private var device = AudioDeviceID(0)
    private var volumeListener: AudioObjectPropertyListenerBlock?
    private var muteListener: AudioObjectPropertyListenerBlock?
    private(set) var gain: Float = 1

    private var volumeAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: kAudioHardwareServiceDeviceProperty_VirtualMainVolume,
            mScope: kAudioObjectPropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain)
    }
    private var muteAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyMute,
            mScope: kAudioObjectPropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain)
    }

    init(queue: DispatchQueue) { self.queue = queue }

    func start(device: AudioDeviceID) {
        stop()
        self.device = device
        refresh()

        var volume = volumeAddress
        if AudioObjectHasProperty(device, &volume) {
            let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.refresh() }
            if AudioObjectAddPropertyListenerBlock(device, &volume, queue, listener) == noErr {
                volumeListener = listener
            }
        }
        var mute = muteAddress
        if AudioObjectHasProperty(device, &mute) {
            let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.refresh() }
            if AudioObjectAddPropertyListenerBlock(device, &mute, queue, listener) == noErr {
                muteListener = listener
            }
        }
    }

    func stop() {
        guard device != 0 else { return }
        if let volumeListener {
            var address = volumeAddress
            AudioObjectRemovePropertyListenerBlock(device, &address, queue, volumeListener)
        }
        if let muteListener {
            var address = muteAddress
            AudioObjectRemovePropertyListenerBlock(device, &address, queue, muteListener)
        }
        volumeListener = nil
        muteListener = nil
        device = 0
        gain = 1
    }

    private func refresh() {
        guard device != 0 else { return }
        var scalar: Float32 = 1
        var scalarAddress = volumeAddress
        var scalarSize = UInt32(MemoryLayout<Float32>.size)
        guard AudioObjectGetPropertyData(device, &scalarAddress, 0, nil, &scalarSize, &scalar) == noErr else {
            gain = 1
            return
        }

        var muted: UInt32 = 0
        var mute = muteAddress
        var muteSize = UInt32(MemoryLayout<UInt32>.size)
        if AudioObjectHasProperty(device, &mute) {
            _ = AudioObjectGetPropertyData(device, &mute, 0, nil, &muteSize, &muted)
        }

        var decibels: Float32 = 0
        var decibelAddress = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeDecibels,
            mScope: kAudioObjectPropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain)
        var decibelSize = UInt32(MemoryLayout<Float32>.size)
        let hasDecibels = AudioObjectGetPropertyData(
            device, &decibelAddress, 0, nil, &decibelSize, &decibels) == noErr
        gain = Self.amplitude(scalar: scalar, decibels: hasDecibels ? decibels : nil, muted: muted != 0)
    }

    static func amplitude(scalar: Float32, decibels: Float32?, muted: Bool) -> Float {
        guard !muted, scalar > 0 else { return 0 }
        guard let decibels, decibels.isFinite else { return min(1, max(0, scalar)) }
        return min(1, max(0, pow(10, decibels / 20)))
    }

    deinit { stop() }
}
