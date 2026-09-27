import Foundation

final class MicrophoneOutput {
    private var opened = false

    private func start() throws {
        guard !opened else { return }
        let result = twindesk_mic_open()
        guard result == 0 else {
            throw BridgeError.message("TwinDesk Microphone could not start (\(result)). Install its audio driver first.")
        }
        opened = true
    }

    @discardableResult func receive(_ data: Data) throws ->
        (received: Float, writtenFrames: Int, bufferedFrames: Int) {
        guard !data.isEmpty, data.count <= 19200, data.count % 4 == 0 else {
            throw BridgeError.message("Invalid microphone audio packet.")
        }
        try start()
        var peak: Float = 0
        var buffered: UInt32 = 0
        let written = data.withUnsafeBytes { bytes in
            twindesk_mic_write_s16le(bytes.baseAddress, UInt32(bytes.count), &peak, &buffered)
        }
        return (peak, Int(written), Int(buffered))
    }

    func stop() {
        guard opened else { return }
        twindesk_mic_close()
        opened = false
    }

    deinit { stop() }
}
