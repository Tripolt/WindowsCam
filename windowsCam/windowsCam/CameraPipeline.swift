import AVFoundation
import Combine
import Foundation
import UIKit

// MARK: - Output Configuration

enum OutputResolution: String, CaseIterable, Identifiable {
    case uhd4K   = "4K"
    case fhd1080 = "1080p"
    case hd720   = "720p"

    var id: String { rawValue }

    var sessionPreset: AVCaptureSession.Preset {
        switch self {
        case .uhd4K:   return .hd4K3840x2160
        case .fhd1080: return .hd1920x1080
        case .hd720:   return .hd1280x720
        }
    }

    var label: String { rawValue }

    var nominalWidth: Int {
        switch self {
        case .uhd4K:   return 3840
        case .fhd1080: return 1920
        case .hd720:   return 1280
        }
    }

    var nominalHeight: Int {
        switch self {
        case .uhd4K:   return 2160
        case .fhd1080: return 1080
        case .hd720:   return 720
        }
    }
}

enum OutputFrameRate: Int, CaseIterable, Identifiable {
    case fps60 = 60
    case fps30 = 30
    case fps24 = 24

    var id: Int { rawValue }

    var label: String { "\(rawValue) fps" }

    var frameDuration: CMTime {
        CMTime(value: 1, timescale: CMTimeScale(rawValue))
    }
}

// MARK: - Camera Pipeline

final class CameraPipeline: NSObject, ObservableObject {
    @Published private(set) var isRunning = false
    @Published private(set) var captureStatus = "Camera idle"
    @Published private(set) var streamStatus = "Stream idle"
    @Published var selectedResolution: OutputResolution = .uhd4K
    @Published var selectedFrameRate: OutputFrameRate = .fps30

    let session = AVCaptureSession()

    private let sessionQueue = DispatchQueue(label: "windowsCam.camera.session")
    private let videoQueue = DispatchQueue(label: "windowsCam.camera.video")
    private let encoder = H264Encoder()
    private let streamServer = StreamServer()
    private var configured = false
    private var currentDevice: AVCaptureDevice?
    private var cancellables = Set<AnyCancellable>()

    override init() {
        super.init()

        encoder.onEncodedFrame = { [weak self] frame in
            self?.streamServer.broadcast(frame)
        }

        encoder.onFormatChange = { [weak self] width, height, fps in
            self?.streamServer.updateHello(width: width, height: height, fps: fps)
            DispatchQueue.main.async {
                self?.captureStatus = "\(width)×\(height) \(fps)fps"
            }
        }

        streamServer.onStatusChange = { [weak self] status in
            self?.streamStatus = status
        }

        // React to setting changes while running
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
                    self.captureStatus = "Camera starting"
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
            self.encoder.invalidate()
            self.streamServer.stop()

            DispatchQueue.main.async {
                UIApplication.shared.isIdleTimerDisabled = false
                self.isRunning = false
                self.captureStatus = "Camera stopped"
                self.streamStatus = "Stream stopped"
            }
        }
    }

    /// Reconfigure the session on-the-fly when resolution or frame rate changes.
    private func reconfigure() {
        sessionQueue.async {
            guard self.configured else { return }

            self.session.beginConfiguration()

            // Update preset
            let preset = self.selectedResolution.sessionPreset
            if self.session.canSetSessionPreset(preset) {
                self.session.sessionPreset = preset
            }

            // Update frame rate
            if let device = self.currentDevice {
                try? self.configureFrameRate(on: device)
            }

            self.session.commitConfiguration()

            // Re-create encoder for new dimensions
            self.encoder.invalidate()
            self.encoder.updateFps(Int32(self.selectedFrameRate.rawValue))

            DispatchQueue.main.async {
                let res = self.selectedResolution
                let fps = self.selectedFrameRate
                self.captureStatus = "\(res.nominalWidth)×\(res.nominalHeight) \(fps.rawValue)fps"
            }
        }
    }

    private func configureIfNeeded() throws {
        guard !configured else { return }

        session.beginConfiguration()
        defer { session.commitConfiguration() }

        let preset = selectedResolution.sessionPreset
        if session.canSetSessionPreset(preset) {
            session.sessionPreset = preset
        } else if session.canSetSessionPreset(.hd1920x1080) {
            session.sessionPreset = .hd1920x1080
        } else {
            session.sessionPreset = .high
        }

        guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back) ??
            AVCaptureDevice.default(.builtInDualWideCamera, for: .video, position: .back) ??
            AVCaptureDevice.default(.builtInTripleCamera, for: .video, position: .back) else {
            throw CameraPipelineError.noCamera
        }

        currentDevice = device

        try configureAutomaticCameraFeatures(on: device)
        try configureFrameRate(on: device)

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

        if let connection = output.connection(with: .video) {
            if connection.isVideoRotationAngleSupported(90) {
                connection.videoRotationAngle = 90
            }
            if connection.isVideoStabilizationSupported {
                connection.preferredVideoStabilizationMode = .auto
            }
        }

        encoder.updateFps(Int32(selectedFrameRate.rawValue))
        configured = true
    }

    private func configureAutomaticCameraFeatures(on device: AVCaptureDevice) throws {
        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }

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
    }

    private func configureFrameRate(on device: AVCaptureDevice) throws {
        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }

        let targetFrameDuration = selectedFrameRate.frameDuration
        device.activeVideoMinFrameDuration = targetFrameDuration
        device.activeVideoMaxFrameDuration = targetFrameDuration
    }
}

extension CameraPipeline: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        encoder.encode(sampleBuffer)
    }
}

enum CameraPipelineError: LocalizedError {
    case noCamera
    case cannotAddInput
    case cannotAddOutput

    var errorDescription: String? {
        switch self {
        case .noCamera:
            "No rear camera found"
        case .cannotAddInput:
            "Could not add camera input"
        case .cannotAddOutput:
            "Could not add video output"
        }
    }
}
