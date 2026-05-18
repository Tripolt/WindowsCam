import AVFoundation
import Foundation
import VideoToolbox

final class H264Encoder {
    var onEncodedFrame: ((Data) -> Void)?
    var onFormatChange: ((Int, Int, Int) -> Void)?

    private let queue = DispatchQueue(label: "windowsCam.h264.encoder")
    private var session: VTCompressionSession?
    private var width: Int32 = 0
    private var height: Int32 = 0
    private var frameCount: Int64 = 0
    private var fps: Int32 = 30

    func updateFps(_ newFps: Int32) {
        queue.async {
            self.fps = newFps
        }
    }

    func encode(_ sampleBuffer: CMSampleBuffer) {
        guard let imageBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let imageWidth = Int32(CVPixelBufferGetWidth(imageBuffer))
        let imageHeight = Int32(CVPixelBufferGetHeight(imageBuffer))
        let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)

        queue.async {
            if self.session == nil || self.width != imageWidth || self.height != imageHeight {
                self.configure(width: imageWidth, height: imageHeight)
            }

            guard let session = self.session else { return }
            let keyFrameInterval = max(Int64(self.fps / 2), 1)
            let forceKeyFrame = self.frameCount % keyFrameInterval == 0
            let options = [kVTEncodeFrameOptionKey_ForceKeyFrame: forceKeyFrame] as CFDictionary
            VTCompressionSessionEncodeFrame(
                session,
                imageBuffer: imageBuffer,
                presentationTimeStamp: presentationTime,
                duration: CMTime(value: 1, timescale: self.fps),
                frameProperties: options,
                sourceFrameRefcon: nil,
                infoFlagsOut: nil
            )
            self.frameCount += 1
        }
    }

    func invalidate() {
        queue.async {
            if let session = self.session {
                VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
                VTCompressionSessionInvalidate(session)
            }
            self.session = nil
            self.frameCount = 0
        }
    }

    private func configure(width: Int32, height: Int32) {
        if let session {
            VTCompressionSessionInvalidate(session)
        }

        self.width = width
        self.height = height
        self.frameCount = 0

        var newSession: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: compressionCallback,
            refcon: UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque()),
            compressionSessionOut: &newSession
        )

        guard status == noErr, let newSession else { return }

        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_High_AutoLevel)
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_MaxFrameDelayCount, value: NSNumber(value: 0))
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: max(fps / 2, 1)))
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, value: NSNumber(value: 0.5))
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: fps))
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: bitrate(forWidth: width, height: height)))
        VTCompressionSessionPrepareToEncodeFrames(newSession)

        session = newSession
        onFormatChange?(Int(width), Int(height), Int(fps))
    }

    fileprivate func handleEncoded(sampleBuffer: CMSampleBuffer) {
        guard CMSampleBufferDataIsReady(sampleBuffer),
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
              let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else {
            return
        }

        let isKeyFrame = !Self.attachmentIsTrue(attachments.first?[kCMSampleAttachmentKey_NotSync])
        var frame = Data()

        if isKeyFrame {
            appendParameterSets(from: sampleBuffer, to: &frame)
        }

        var totalLength = 0
        var dataPointer: UnsafeMutablePointer<Int8>?
        guard CMBlockBufferGetDataPointer(
            blockBuffer,
            atOffset: 0,
            lengthAtOffsetOut: nil,
            totalLengthOut: &totalLength,
            dataPointerOut: &dataPointer
        ) == noErr, let dataPointer else {
            return
        }

        var offset = 0
        let lengthHeaderSize = 4

        while offset + lengthHeaderSize < totalLength {
            var nalLength: UInt32 = 0
            memcpy(&nalLength, dataPointer + offset, lengthHeaderSize)
            nalLength = UInt32(bigEndian: nalLength)
            offset += lengthHeaderSize

            guard offset + Int(nalLength) <= totalLength else { break }
            frame.append(Self.startCode)
            frame.append(Data(bytes: dataPointer + offset, count: Int(nalLength)))
            offset += Int(nalLength)
        }

        if !frame.isEmpty {
            onEncodedFrame?(frame)
        }
    }

    private func appendParameterSets(from sampleBuffer: CMSampleBuffer, to frame: inout Data) {
        guard let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer) else { return }

        for index in 0..<2 {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            var count = 0

            let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                formatDescription,
                parameterSetIndex: index,
                parameterSetPointerOut: &pointer,
                parameterSetSizeOut: &size,
                parameterSetCountOut: &count,
                nalUnitHeaderLengthOut: nil
            )

            if status == noErr, let pointer {
                frame.append(Self.startCode)
                frame.append(Data(bytes: pointer, count: size))
            }
        }
    }

    private func bitrate(forWidth width: Int32, height: Int32) -> Int {
        width >= 3840 || height >= 2160 ? 24_000_000 : 12_000_000
    }

    private static func attachmentIsTrue(_ value: Any?) -> Bool {
        guard let value else { return false }

        if let bool = value as? Bool {
            return bool
        }

        if CFGetTypeID(value as CFTypeRef) == CFBooleanGetTypeID() {
            return CFBooleanGetValue(value as! CFBoolean)
        }

        return false
    }

    private static let startCode = Data([0, 0, 0, 1])
}

private let compressionCallback: VTCompressionOutputCallback = { refcon, _, status, _, sampleBuffer in
    guard status == noErr,
          let refcon,
          let sampleBuffer else {
        return
    }

    let encoder = Unmanaged<H264Encoder>.fromOpaque(refcon).takeUnretainedValue()
    encoder.handleEncoded(sampleBuffer: sampleBuffer)
}
