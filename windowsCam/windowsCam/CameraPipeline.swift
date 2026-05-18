import AVFoundation
import Combine
import Foundation
import UIKit

final class CameraPipeline: NSObject, ObservableObject {
    @Published private(set) var isRunning = false
    @Published private(set) var captureStatus = "Camera idle"
    @Published private(set) var streamStatus = "Stream idle"

    let session = AVCaptureSession()

    private let sessionQueue = DispatchQueue(label: "windowsCam.camera.session")
    private let videoQueue = DispatchQueue(label: "windowsCam.camera.video")
    private let encoder = H264Encoder()
    private let streamServer = StreamServer()
    private var configured = false

    override init() {
        super.init()

        encoder.onEncodedFrame = { [weak self] frame in
            self?.streamServer.broadcast(frame)
        }

        encoder.onFormatChange = { [weak self] width, height, fps in
            self?.streamServer.updateHello(width: width, height: height, fps: fps)
            DispatchQueue.main.async {
                self?.captureStatus = "\(width)x\(height) \(fps)fps"
            }
        }

        streamServer.onStatusChange = { [weak self] status in
            self?.streamStatus = status
        }
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

    private func configureIfNeeded() throws {
        guard !configured else { return }

        session.beginConfiguration()
        defer { session.commitConfiguration() }

        if session.canSetSessionPreset(.hd4K3840x2160) {
            session.sessionPreset = .hd4K3840x2160
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

        let targetFrameDuration = CMTime(value: 1, timescale: 30)
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
