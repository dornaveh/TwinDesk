import Foundation
import CoreAudio

func convert(_ channels: [[Float]], interleaved: Bool = false) -> [Int16]? {
    let buffers = AudioBufferList.allocate(maximumBuffers: channels.count)
    defer { buffers.unsafeMutablePointer.deallocate() }
    var allocations: [UnsafeMutablePointer<Float>] = []
    defer { allocations.forEach { $0.deallocate() } }
    for (index, samples) in channels.enumerated() {
        let data = UnsafeMutablePointer<Float>.allocate(capacity: max(samples.count, 1))
        allocations.append(data)
        for (offset, sample) in samples.enumerated() { data[offset] = sample }
        buffers[index] = AudioBuffer(mNumberChannels: interleaved ? 2 : 1, mDataByteSize: UInt32(samples.count * 4), mData: data)
    }
    return TapPCM.encode(buffers)?.withUnsafeBytes { Array($0.bindMemory(to: Int16.self)) }
}
precondition(convert([[1, -1, 0.5, -0.5]], interleaved: true) == [32767, -32767, 16383, -16383])
print("PASS interleaved stereo preserves left/right order")
precondition(convert([[1, 0.5], [-1, -0.5]]) == [32767, -32767, 16383, -16383])
print("PASS planar stereo becomes interleaved PCM")
precondition(convert([[2, -2, .nan, .infinity]], interleaved: true) == [32767, -32768, 0, 0])
print("PASS clipping and nonfinite samples handled")
precondition(convert([[1, 2], [1]]) == nil)
precondition(convert([[1]]) == nil)
print("PASS mismatched buffers and mono input rejected")
