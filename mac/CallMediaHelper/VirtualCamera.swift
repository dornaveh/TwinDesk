import Foundation
import CoreMediaIO
import CoreMedia
import CoreVideo
import ImageIO
import CoreGraphics

// Sends frames to OBS's separately installed, signed camera extension.
// No OBS code or binary is embedded. The stable device UID is its interoperability contract.
final class VirtualCamera {
    var framesPerSecond: Double = 30
    var inputWidth = 1920
    var inputHeight = 1080
    static let obsUID = "7626645E-4425-469E-9D8B-97E0FA59AC75"
    private var device: CMIODeviceID = 0
    private var stream: CMIOStreamID = 0
    private var buffers: CMSimpleQueue?
    private var pool: CVPixelBufferPool?
    private var format: CMVideoFormatDescription?

    private func address(_ selector: UInt32) -> CMIOObjectPropertyAddress {
        CMIOObjectPropertyAddress(mSelector: selector, mScope: UInt32(kCMIOObjectPropertyScopeGlobal), mElement: UInt32(kCMIOObjectPropertyElementMain))
    }
    private func ids(_ object: CMIOObjectID, _ selector: UInt32) throws -> [UInt32] {
        var property = address(selector), size: UInt32 = 0
        guard CMIOObjectGetPropertyDataSize(object, &property, 0, nil, &size) == noErr,
              size <= 16384, size % 4 == 0 else { throw BridgeError.message("Cannot enumerate virtual camera devices.") }
        var values = [UInt32](repeating: 0, count: Int(size / 4)), used: UInt32 = 0
        guard CMIOObjectGetPropertyData(object, &property, 0, nil, size, &used, &values) == noErr else { throw BridgeError.message("Cannot read virtual camera devices.") }
        return Array(values.prefix(Int(used / 4)))
    }
    private func open() throws {
        if stream != 0 { return }
        for candidate in try ids(CMIOObjectID(kCMIOObjectSystemObject), UInt32(kCMIOHardwarePropertyDevices)) {
            var property = address(UInt32(kCMIODevicePropertyDeviceUID)), uid: Unmanaged<CFString>?, used: UInt32 = 0
            guard CMIOObjectGetPropertyData(candidate, &property, 0, nil, UInt32(MemoryLayout<Unmanaged<CFString>?>.size), &used, &uid) == noErr,
                  let uid, uid.takeRetainedValue() as String == Self.obsUID else { continue }
            device = candidate; break
        }
        guard device != 0 else { throw BridgeError.message("Install OBS 30 or later and enable its Virtual Camera once, then close OBS and retry.") }
        for candidate in try ids(device, UInt32(kCMIODevicePropertyStreams)) {
            var property = address(UInt32(kCMIOStreamPropertyDirection)), direction: UInt32 = 1, used: UInt32 = 0
            if CMIOObjectGetPropertyData(candidate, &property, 0, nil, 4, &used, &direction) == noErr && direction == 0 { stream = candidate; break }
        }
        guard stream != 0 else { throw BridgeError.message("OBS Virtual Camera has no writable stream. Update OBS and enable its camera extension.") }
        var queue: Unmanaged<CMSimpleQueue>?
        guard CMIOStreamCopyBufferQueue(stream, { _, _, _ in }, nil, &queue) == noErr, let queue else {
            stream = 0; throw BridgeError.message("Cannot open the OBS camera queue.")
        }
        buffers = queue.takeRetainedValue()
        let attributes: [CFString: Any] = [kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey: 1920, kCVPixelBufferHeightKey: 1080,
            kCVPixelBufferCGImageCompatibilityKey: true, kCVPixelBufferCGBitmapContextCompatibilityKey: true,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as [String: Any]]
        guard CVPixelBufferPoolCreate(nil, nil, attributes as CFDictionary, &pool) == kCVReturnSuccess,
              CMVideoFormatDescriptionCreate(allocator: nil, codecType: kCVPixelFormatType_32BGRA, width: 1920, height: 1080, extensions: nil, formatDescriptionOut: &format) == noErr else {
            stop(); throw BridgeError.message("Cannot allocate virtual camera buffers.")
        }
        let result = CMIODeviceStartStream(device, stream)
        guard result == noErr else { stop(); throw BridgeError.message("Cannot start OBS Virtual Camera (\(result)). Stop any other app feeding it and retry.") }
    }
    func send(jpeg: Data) throws {
        // Inspect dimensions before decoding; reject malformed or unexpectedly large images.
        guard jpeg.count <= 4_194_304, jpeg.starts(with: [0xff, 0xd8]),
              let source = CGImageSourceCreateWithData(jpeg as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              properties[kCGImagePropertyPixelWidth] as? Int == inputWidth,
              properties[kCGImagePropertyPixelHeight] as? Int == inputHeight else { throw BridgeError.message("The PC sent an invalid camera image.") }
        try open()
        guard let buffers, let pool, let format else { return }
        // Never build a backlog if the extension or consumer stalls.
        guard CMSimpleQueueGetCount(buffers) < min(2, CMSimpleQueueGetCapacity(buffers)) else { return }
        guard let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw BridgeError.message("Cannot decode camera image.") }
        var pixel: CVPixelBuffer?
        let options = [kCVPixelBufferPoolAllocationThresholdKey: 4] as CFDictionary
        guard CVPixelBufferPoolCreatePixelBufferWithAuxAttributes(nil, pool, options, &pixel) == kCVReturnSuccess, let pixel else { return }
        CVPixelBufferLockBaseAddress(pixel, [])
        guard let context = CGContext(data: CVPixelBufferGetBaseAddress(pixel), width: 1920, height: 1080, bitsPerComponent: 8,
                                      bytesPerRow: CVPixelBufferGetBytesPerRow(pixel), space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else {
            CVPixelBufferUnlockBaseAddress(pixel, []); throw BridgeError.message("Cannot convert camera image.")
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: 1920, height: 1080))
        CVPixelBufferUnlockBaseAddress(pixel, [])
        var timing = CMSampleTimingInfo(duration: CMTime(seconds: 1 / framesPerSecond, preferredTimescale: 60000), presentationTimeStamp: CMClockGetTime(CMClockGetHostTimeClock()), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        guard CMSampleBufferCreateForImageBuffer(allocator: nil, imageBuffer: pixel, dataReady: true, makeDataReadyCallback: nil, refcon: nil,
                formatDescription: format, sampleTiming: &timing, sampleBufferOut: &sample) == noErr, let sample else { return }
        // CMIO consumes this retained sample; recover ownership if enqueue fails.
        let retained = Unmanaged.passRetained(sample)
        if CMSimpleQueueEnqueue(buffers, element: retained.toOpaque()) != noErr { retained.release() }
    }
    func stop() {
        if stream != 0 { CMIODeviceStopStream(device, stream) }
        stream = 0; device = 0; buffers = nil; pool = nil; format = nil
    }
    deinit { stop() }
}
