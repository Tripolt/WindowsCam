import AVFoundation
import Combine
import Foundation
import UIKit

// MARK: - Output Configuration

enum OutputResolution: String, CaseIterable, Identifiable {
    case uhd4K = "4K"
    case fhd1080 = "1080p"
    case hd720 = "720p"

    var id: String { rawValue }
    var label: String { rawValue }

    var width: Int {
        switch self {
        case .uhd4K: 3840
        case .fhd1080: 1920
        case .hd720: 1280
        }
    }

    var height: Int {
        switch self {
        case .uhd4K: 2160
        case .fhd1080: 1080
        case .hd720: 720
        }
    }
}

enum OutputFrameRate: Int, CaseIterable, Identifiable {
    case fps60 = 60
    case fps40 = 40
    case fps30 = 30

    var id: Int { rawValue }
    var label: String { "\(rawValue) fps" }

    var frameDuration: CMTime {
        CMTime(value: 1, timescale: CMTimeScale(rawValue))
    }
}

struct RawVideoMode: Equatable, Hashable {
    let resolution: OutputResolution
    let frameRate: OutputFrameRate

    var width: Int { resolution.width }
    var height: Int { resolution.height }
    var fps: Int { frameRate.rawValue }
    var bytesPerFrame: Int { width * height * 3 / 2 }
    var bytesPerSecond: Int { bytesPerFrame * fps }
    var id: String { "\(width)x\(height)@\(fps)" }
    var label: String { "\(resolution.label) @ \(frameRate.label)" }

    static let preferredOrder: [RawVideoMode] = [
        RawVideoMode(resolution: .uhd4K, frameRate: .fps40),
        RawVideoMode(resolution: .uhd4K, frameRate: .fps30),
        RawVideoMode(resolution: .fhd1080, frameRate: .fps60),
        RawVideoMode(resolution: .fhd1080, frameRate: .fps40),
        RawVideoMode(resolution: .fhd1080, frameRate: .fps30),
        RawVideoMode(resolution: .hd720, frameRate: .fps60),
        RawVideoMode(resolution: .hd720, frameRate: .fps40),
        RawVideoMode(resolution: .hd720, frameRate: .fps30)
    ]
}

// MARK: - Camera Pipeline

final class CameraPipeline: NSObject, ObservableObject {
    @Published private(set) var isRunning = false
    @Published private(set) var captureStatus = "Camera idle"
    @Published private(set) var streamStatus = "PC disconnected"
    @Published private(set) var supportedModeIDs: Set<String> = []
    @Published var selectedResolution: OutputResolution = .uhd4K
    @Published var selectedFrameRate: OutputFrameRate = .fps40

    let session = AVCaptureSession()

    private let sessionQueue = DispatchQueue(label: "windowsCam.camera.session")
    private let videoQueue = DispatchQueue(label: "windowsCam.camera.video", qos: .userInteractive)
    private let streamServer = StreamServer()
    private let modeLock = NSLock()
    private var configured = false
    private var currentDevice: AVCaptureDevice?
    private var currentMode = RawVideoMode(resolution: .uhd4K, frameRate: .fps40)
    private var captureStatsStart = Date()
    private var captureStatsFrames = 0
    private var cancellables = Set<AnyCancellable>()

    override init() {
        super.init()

        streamServer.onStatusChange = { [weak self] status in
            self?.streamStatus = status
        }

        $selectedResolution
            .dropFirst()
            .sink { [weak self] _ in self?.reconfigure() }
            .store(in: &cancellables)

        $selectedFrameRate
            .dropFirst()
            .sink { [weak self] _ in self?.reconfigure() }
            .store(in: &cancellables)
    }

    func requestAccessAndStart() async {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            start()
        case .notDetermined:
            if await AVCaptureDevice.requestAccess(for: .video) {
                start()
            } else {
                await MainActor.run {
                    captureStatus = "Camera denied"
                }
            }
        default:
            await MainActor.run {
                captureStatus = "Camera denied"
            }
        }
    }

    func isResolutionAvailable(_ resolution: OutputResolution) -> Bool {
        if supportedModeIDs.isEmpty {
            return true
        }

        return OutputFrameRate.allCases.contains { fps in
            supportedModeIDs.contains(RawVideoMode(resolution: resolution, frameRate: fps).id)
        }
    }

    func isFrameRateAvailable(_ frameRate: OutputFrameRate) -> Bool {
        if supportedModeIDs.isEmpty {
            return true
        }

        return supportedModeIDs.contains(RawVideoMode(resolution: selectedResolution, frameRate: frameRate).id)
    }

    func start() {
        sessionQueue.async {
            guard !self.session.isRunning else { return }

            do {
                try self.configureIfNeeded()
                self.streamServer.start()
                self.session.startRunning()
                DispatchQueue.main.async {
                    UIApplication.shared.isIdleTimerDisabled = true
                    self.isRunning = true
                    self.captureStatus = "\(self.readCurrentMode().label) raw"
                }
            } catch {
                DispatchQueue.main.async {
                    self.captureStatus = "Camera error"
                    self.streamStatus = error.localizedDescription
                }
            }
        }
    }

    func stop() {
        sessionQueue.async {
            guard self.session.isRunning else { return }
            self.session.stopRunning()
            self.streamServer.stop()

            DispatchQueue.main.async {
                UIApplication.shared.isIdleTimerDisabled = false
                self.isRunning = false
                self.captureStatus = "Camera stopped"
                self.streamStatus = "PC disconnected"
            }
        }
    }

    func restart() {
        sessionQueue.async {
            if self.session.isRunning {
                self.session.stopRunning()
            }

            self.streamServer.stop()
            self.configured = false
            self.session.inputs.forEach { self.session.removeInput($0) }
            self.session.outputs.forEach { self.session.removeOutput($0) }

            do {
                try self.configureIfNeeded()
                self.streamServer.start()
                self.session.startRunning()
                DispatchQueue.main.async {
                    UIApplication.shared.isIdleTimerDisabled = true
                    self.isRunning = true
                    self.captureStatus = "\(self.readCurrentMode().label) raw"
                }
            } catch {
                DispatchQueue.main.async {
                    self.captureStatus = "Camera error"
                    self.streamStatus = error.localizedDescription
                }
            }
        }
    }

    private func reconfigure() {
        sessionQueue.async {
            guard self.configured, let device = self.currentDevice else { return }

            do {
                self.session.beginConfiguration()
                defer { self.session.commitConfiguration() }
                let mode = try self.configureDeviceForSelectedMode(device)
                self.publishActiveMode(mode)
                DispatchQueue.main.async {
                    self.captureStatus = "\(mode.label) raw"
                }
            } catch {
                DispatchQueue.main.async {
                    self.captureStatus = "Mode unavailable"
                    self.streamStatus = error.localizedDescription
                }
            }
        }
    }

    private func configureIfNeeded() throws {
        guard !configured else { return }

        guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back) ??
            AVCaptureDevice.default(.builtInDualWideCamera, for: .video, position: .back) ??
            AVCaptureDevice.default(.builtInTripleCamera, for: .video, position: .back) else {
            throw CameraPipelineError.noCamera
        }

        currentDevice = device
        let supportedModes = Self.supportedModes(on: device)
        DispatchQueue.main.async {
            self.supportedModeIDs = Set(supportedModes.map { $0.id })
        }

        session.beginConfiguration()
        defer { session.commitConfiguration() }

        if session.canSetSessionPreset(.inputPriority) {
            session.sessionPreset = .inputPriority
        }

        let mode = try configureDeviceForSelectedMode(device, supportedModes: supportedModes)

        let input = try AVCaptureDeviceInput(device: device)
        guard session.canAddInput(input) else { throw CameraPipelineError.cannotAddInput }
        session.addInput(input)

        let output = AVCaptureVideoDataOutput()
        output.alwaysDiscardsLateVideoFrames = true
        output.videoSettings = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        ]
        output.setSampleBufferDelegate(self, queue: videoQueue)

        guard session.canAddOutput(output) else { throw CameraPipelineError.cannotAddOutput }
        session.addOutput(output)

        if let connection = output.connection(with: .video), connection.isVideoStabilizationSupported {
            connection.preferredVideoStabilizationMode = .off
        }

        publishActiveMode(mode)
        configured = true
    }

    private func configureDeviceForSelectedMode(
        _ device: AVCaptureDevice,
        supportedModes: [RawVideoMode]? = nil
    ) throws -> RawVideoMode {
        let modes = supportedModes ?? Self.supportedModes(on: device)
        guard !modes.isEmpty else { throw CameraPipelineError.noSupportedMode }

        let selectedMode = RawVideoMode(resolution: selectedResolution, frameRate: selectedFrameRate)
        let mode = modes.contains(selectedMode) ? selectedMode : modes[0]
        guard let format = Self.format(on: device, for: mode) else { throw CameraPipelineError.noSupportedMode }

        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }

        device.activeFormat = format
        device.activeVideoMinFrameDuration = mode.frameRate.frameDuration
        device.activeVideoMaxFrameDuration = mode.frameRate.frameDuration

        if device.isFocusModeSupported(.continuousAutoFocus) {
            device.focusMode = .continuousAutoFocus
        }
        if device.isExposureModeSupported(.continuousAutoExposure) {
            device.exposureMode = .continuousAutoExposure
        }
        if device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) {
            device.whiteBalanceMode = .continuousAutoWhiteBalance
        }
        if device.isSmoothAutoFocusSupported {
            device.isSmoothAutoFocusEnabled = true
        }

        if mode != selectedMode {
            DispatchQueue.main.async {
                self.selectedResolution = mode.resolution
                self.selectedFrameRate = mode.frameRate
            }
        }

        return mode
    }

    private func publishActiveMode(_ mode: RawVideoMode) {
        modeLock.lock()
        currentMode = mode
        modeLock.unlock()

        streamServer.updateMode(mode, supportedModes: Self.supportedModes(on: currentDevice))
    }

    private func readCurrentMode() -> RawVideoMode {
        modeLock.lock()
        defer { modeLock.unlock() }
        return currentMode
    }

    private static func supportedModes(on device: AVCaptureDevice?) -> [RawVideoMode] {
        guard let device else { return RawVideoMode.preferredOrder }
        let supported = RawVideoMode.preferredOrder.filter { format(on: device, for: $0) != nil }
        return supported.isEmpty ? [] : supported
    }

    private static func format(on device: AVCaptureDevice, for mode: RawVideoMode) -> AVCaptureDevice.Format? {
        let matching = device.formats.filter { format in
            let description = format.formatDescription
            let dimensions = CMVideoFormatDescriptionGetDimensions(description)
            guard Int(dimensions.width) == mode.width, Int(dimensions.height) == mode.height else {
                return false
            }

            let pixelFormat = CMFormatDescriptionGetMediaSubType(description)
            guard pixelFormat == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange ||
                    pixelFormat == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange else {
                return false
            }

            return format.videoSupportedFrameRateRanges.contains { range in
                range.minFrameRate <= Double(mode.fps) && range.maxFrameRate >= Double(mode.fps)
            }
        }

        return matching.sorted { lhs, rhs in
            let lhsMax = lhs.videoSupportedFrameRateRanges.map { $0.maxFrameRate }.max() ?? 0
            let rhsMax = rhs.videoSupportedFrameRateRanges.map { $0.maxFrameRate }.max() ?? 0
            return lhsMax < rhsMax
        }.first
    }

    private func recordCapturedFrame(mode: RawVideoMode) {
        captureStatsFrames += 1
        let elapsed = Date().timeIntervalSince(captureStatsStart)
        guard elapsed >= 1 else { return }

        let fps = Double(captureStatsFrames) / elapsed
        captureStatsFrames = 0
        captureStatsStart = Date()

        let roundedFps = Int(fps.rounded())
        DispatchQueue.main.async {
            self.captureStatus = "\(mode.label) raw \(roundedFps) fps"
        }
    }
}

extension CameraPipeline: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let imageBuffer = CMSampleBufferGetImageBuffer(sampleBuffer),
              let frame = Self.copyNV12Frame(from: imageBuffer, fps: readCurrentMode().fps) else {
            return
        }

        let mode = readCurrentMode()
        if frame.width != mode.width || frame.height != mode.height {
            let actualMode = RawVideoMode(
                resolution: OutputResolution.allCases.first { $0.width == frame.width && $0.height == frame.height } ?? mode.resolution,
                frameRate: mode.frameRate
            )
            publishActiveMode(actualMode)
        }

        recordCapturedFrame(mode: readCurrentMode())
        streamServer.broadcast(frame)
    }

    private static func copyNV12Frame(from imageBuffer: CVPixelBuffer, fps: Int) -> RawVideoFrame? {
        CVPixelBufferLockBaseAddress(imageBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(imageBuffer, .readOnly) }

        guard CVPixelBufferGetPlaneCount(imageBuffer) == 2,
              let yBase = CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 0),
              let uvBase = CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 1) else {
            return nil
        }

        let width = CVPixelBufferGetWidth(imageBuffer)
        let height = CVPixelBufferGetHeight(imageBuffer)
        let yStride = CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 0)
        let uvStride = CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 1)
        let yRows = CVPixelBufferGetHeightOfPlane(imageBuffer, 0)
        let uvRows = CVPixelBufferGetHeightOfPlane(imageBuffer, 1)

        guard width > 0, height > 0, width % 2 == 0, height % 2 == 0,
              yRows == height, uvRows == height / 2 else {
            return nil
        }

        var frame = Data(count: width * height * 3 / 2)
        frame.withUnsafeMutableBytes { output in
            guard let destination = output.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
            let ySource = yBase.assumingMemoryBound(to: UInt8.self)
            let uvSource = uvBase.assumingMemoryBound(to: UInt8.self)

            for row in 0..<height {
                memcpy(destination + row * width, ySource + row * yStride, width)
            }

            let uvDestination = destination + width * height
            for row in 0..<(height / 2) {
                memcpy(uvDestination + row * width, uvSource + row * uvStride, width)
            }
        }

        return RawVideoFrame(
            payload: frame,
            width: width,
            height: height,
            fps: fps,
            lumaStride: width,
            capturedAtNs: DispatchTime.now().uptimeNanoseconds
        )
    }
}

enum CameraPipelineError: LocalizedError {
    case noCamera
    case noSupportedMode
    case cannotAddInput
    case cannotAddOutput

    var errorDescription: String? {
        switch self {
        case .noCamera:
            "No rear camera found"
        case .noSupportedMode:
            "This iPhone camera does not expose a supported raw NV12 mode"
        case .cannotAddInput:
            "Could not add camera input"
        case .cannotAddOutput:
            "Could not add video output"
        }
    }
}
