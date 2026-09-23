import Foundation
import CoreAudio

enum TapPCM {
    static func encode(_ buffers: UnsafeMutableAudioBufferListPointer) -> Data? {
        let planar = buffers.count == 2 && buffers.allSatisfy { $0.mNumberChannels == 1 }
        guard planar || (buffers.count == 1 && buffers[0].mNumberChannels == 2),
              buffers.allSatisfy({ $0.mData != nil && $0.mDataByteSize % 4 == 0 }) else { return nil }
        let frames = Int(buffers[0].mDataByteSize) / (planar ? 4 : 8)
        guard frames > 0, frames <= 48000,
              !planar || buffers[0].mDataByteSize == buffers[1].mDataByteSize else { return nil }
        var result = Data(count: frames * 4)
        result.withUnsafeMutableBytes { raw in
            let output = raw.bindMemory(to: Int16.self)
            for frame in 0..<frames {
                for channel in 0..<2 {
                    let source = buffers[planar ? channel : 0].mData!.assumingMemoryBound(to: Float.self)
                    let value = source[planar ? frame : frame * 2 + channel]
                    output[frame * 2 + channel] = (value.isFinite ? Int16(max(-32768, min(32767, value * 32767))) : 0).littleEndian
                }
            }
        }
        return result
    }
}
