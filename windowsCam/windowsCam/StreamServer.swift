import Foundation
import Network

struct RawVideoFrame {
    let payload: Data
    let width: Int
    let height: Int
    let fps: Int
    let lumaStride: Int
    let capturedAtNs: UInt64

    var payloadBytes: Int { payload.count }
}

private struct StreamHello: Encodable {
    let version: Int
    let codec: String
    let pixelFormat: String
    let width: Int
    let height: Int
    let fps: Int
    let lumaStride: Int
    let bytesPerFrame: Int
    let bytesPerSecond: Int
    let frameHeaderBytes: Int
    let orientation: String
    let framing: String
    let modes: [StreamMode]
}

private struct StreamMode: Encodable {
    let width: Int
    let height: Int
    let fps: Int
    let bytesPerSecond: Int
}

final class StreamServer {
    static let port: UInt16 = 48650

    var onStatusChange: ((String) -> Void)?

    private static let rawFrameMagic: UInt32 = 0x5746524D // WFRM
    private static let rawFrameVersion: UInt16 = 2
    private static let rawFrameHeaderBytes: UInt16 = 48

    private let queue = DispatchQueue(label: "windowsCam.stream.server", qos: .userInteractive)
    private var listener: NWListener?
    private var clients: [StreamClient] = []
    private var latestMode = RawVideoMode(resolution: .uhd4K, frameRate: .fps40)
    private var supportedModes = RawVideoMode.preferredOrder
    private var sequence: UInt64 = 0
    private var stats = StreamStats()

    func start() {
        queue.async {
            guard self.listener == nil else { return }

            do {
                let tcpOptions = NWProtocolTCP.Options()
                tcpOptions.noDelay = true

                let parameters = NWParameters(tls: nil, tcp: tcpOptions)
                parameters.allowLocalEndpointReuse = true
                let listener = try NWListener(using: parameters, on: NWEndpoint.Port(rawValue: Self.port)!)
                self.listener = listener

                listener.stateUpdateHandler = { [weak self] state in
                    switch state {
                    case .ready:
                        self?.emit("PC camera port \(Self.port)")
                    case .failed(let error):
                        self?.emit("PC link failed: \(error.localizedDescription)")
                        self?.stop()
                    case .cancelled:
                        self?.emit("PC disconnected")
                    default:
                        break
                    }
                }

                listener.newConnectionHandler = { [weak self] connection in
                    self?.accept(connection)
                }

                listener.start(queue: self.queue)
            } catch {
                self.emit("PC link error: \(error.localizedDescription)")
            }
        }
    }

    func stop() {
        queue.async {
            self.listener?.cancel()
            self.listener = nil
            self.clients.forEach { $0.connection.cancel() }
            self.clients.removeAll()
            self.stats.reset()
        }
    }

    func updateMode(_ mode: RawVideoMode, supportedModes: [RawVideoMode]) {
        queue.async {
            let changed = self.latestMode != mode
            self.latestMode = mode
            if !supportedModes.isEmpty {
                self.supportedModes = supportedModes
            }

            guard changed else { return }

            self.clients.forEach { $0.connection.cancel() }
            self.clients.removeAll()
            self.stats.reset()
            self.emit("Raw \(mode.width)x\(mode.height)@\(mode.fps)")
        }
    }

    func broadcast(_ frame: RawVideoFrame) {
        queue.async {
            guard !self.clients.isEmpty else { return }

            if frame.width != self.latestMode.width || frame.height != self.latestMode.height || frame.fps != self.latestMode.fps {
                self.latestMode = RawVideoMode(
                    resolution: OutputResolution.allCases.first { $0.width == frame.width && $0.height == frame.height } ?? self.latestMode.resolution,
                    frameRate: OutputFrameRate(rawValue: frame.fps) ?? self.latestMode.frameRate
                )
                self.clients.forEach { $0.connection.cancel() }
                self.clients.removeAll()
                self.stats.reset()
                return
            }

            self.sequence &+= 1
            let packet = Self.packet(frame, sequence: self.sequence)
            self.clients.forEach { client in
                self.queuePacket(
                    StreamPacket(data: packet, isDroppable: true, payloadBytes: frame.payloadBytes),
                    to: client
                )
            }
        }
    }

    private func accept(_ connection: NWConnection) {
        let client = StreamClient(connection: connection)
        clients.append(client)

        connection.stateUpdateHandler = { [weak self, weak client] state in
            guard let self, let client else { return }

            if case .ready = state {
                self.sendHello(to: client)
                self.emit("\(self.clients.count) PC connection(s)")
            }

            if case .failed = state {
                self.remove(client)
            }

            if case .cancelled = state {
                self.remove(client)
            }
        }

        connection.start(queue: queue)
    }

    private func sendHello(to client: StreamClient) {
        let mode = latestMode
        let hello = StreamHello(
            version: 2,
            codec: "nv12-raw",
            pixelFormat: "nv12",
            width: mode.width,
            height: mode.height,
            fps: mode.fps,
            lumaStride: mode.width,
            bytesPerFrame: mode.bytesPerFrame,
            bytesPerSecond: mode.bytesPerSecond,
            frameHeaderBytes: Int(Self.rawFrameHeaderBytes),
            orientation: "landscapeRight",
            framing: "uint32be-length-prefixed/raw-v2",
            modes: supportedModes.map {
                StreamMode(width: $0.width, height: $0.height, fps: $0.fps, bytesPerSecond: $0.bytesPerSecond)
            }
        )

        guard let data = try? JSONEncoder().encode(hello) else { return }
        queuePacket(
            StreamPacket(data: Self.packet(data), isDroppable: false, payloadBytes: data.count),
            to: client
        )
    }

    private func queuePacket(_ packet: StreamPacket, to client: StreamClient) {
        if client.isSending {
            if packet.isDroppable, client.pendingPacket?.isDroppable == true {
                stats.droppedFrames += 1
            }

            client.pendingPacket = packet
            emitStatsIfNeeded()
            return
        }

        sendNow(packet, to: client)
    }

    private func sendNow(_ packet: StreamPacket, to client: StreamClient) {
        client.isSending = true
        client.connection.send(content: packet.data, completion: .contentProcessed { [weak self, weak client] error in
            guard let self, let client else { return }

            self.queue.async {
                if error != nil {
                    self.remove(client)
                    return
                }

                if packet.isDroppable {
                    self.stats.sentFrames += 1
                    self.stats.sentBytes += packet.payloadBytes
                    self.emitStatsIfNeeded()
                }

                if let pendingPacket = client.pendingPacket {
                    client.pendingPacket = nil
                    self.sendNow(pendingPacket, to: client)
                } else {
                    client.isSending = false
                }
            }
        })
    }

    private func remove(_ client: StreamClient) {
        clients.removeAll { $0 === client }
        emit(clients.isEmpty ? "PC camera port \(Self.port)" : "\(clients.count) PC connection(s)")
    }

    private func emitStatsIfNeeded() {
        let now = Date()
        let elapsed = now.timeIntervalSince(stats.windowStart)
        guard elapsed >= 1 else { return }

        let fps = Double(stats.sentFrames) / elapsed
        let mbps = Double(stats.sentBytes) / elapsed / 1_000_000
        let required = Double(latestMode.bytesPerSecond) / 1_000_000
        let warning = fps < Double(latestMode.fps) * 0.90 || mbps < required * 0.90
        let prefix = warning ? "USB slow" : "Raw link"
        emit("\(prefix) \(Int(mbps.rounded())) MB/s \(Int(fps.rounded())) fps drop \(stats.droppedFrames)")
        stats.reset(keepingDropped: true)
    }

    private func emit(_ status: String) {
        DispatchQueue.main.async {
            self.onStatusChange?(status)
        }
    }

    private static func packet(_ payload: Data) -> Data {
        var length = UInt32(payload.count).bigEndian
        var packet = Data(bytes: &length, count: MemoryLayout<UInt32>.size)
        packet.append(payload)
        return packet
    }

    private static func packet(_ frame: RawVideoFrame, sequence: UInt64) -> Data {
        let payloadLength = Int(rawFrameHeaderBytes) + frame.payload.count
        var packet = Data()
        packet.reserveCapacity(MemoryLayout<UInt32>.size + payloadLength)
        packet.appendUInt32(UInt32(payloadLength))
        packet.appendUInt32(rawFrameMagic)
        packet.appendUInt16(rawFrameVersion)
        packet.appendUInt16(rawFrameHeaderBytes)
        packet.appendUInt32(UInt32(frame.width))
        packet.appendUInt32(UInt32(frame.height))
        packet.appendUInt32(UInt32(frame.fps))
        packet.appendUInt32(UInt32(frame.lumaStride))
        packet.appendUInt32(UInt32(frame.payloadBytes))
        packet.appendUInt64(sequence)
        packet.appendUInt64(frame.capturedAtNs)
        packet.appendUInt32(0)
        frame.payload.withUnsafeBytes { bytes in
            if let baseAddress = bytes.baseAddress {
                packet.append(baseAddress.assumingMemoryBound(to: UInt8.self), count: frame.payload.count)
            }
        }
        return packet
    }
}

private final class StreamClient {
    let connection: NWConnection
    var isSending = false
    var pendingPacket: StreamPacket?

    init(connection: NWConnection) {
        self.connection = connection
    }
}

private struct StreamPacket {
    let data: Data
    let isDroppable: Bool
    let payloadBytes: Int
}

private struct StreamStats {
    var windowStart = Date()
    var sentFrames = 0
    var sentBytes = 0
    var droppedFrames: Int64 = 0

    mutating func reset(keepingDropped: Bool = false) {
        windowStart = Date()
        sentFrames = 0
        sentBytes = 0
        if !keepingDropped {
            droppedFrames = 0
        }
    }
}

private extension Data {
    mutating func appendUInt16(_ value: UInt16) {
        var bigEndian = value.bigEndian
        append(Data(bytes: &bigEndian, count: MemoryLayout<UInt16>.size))
    }

    mutating func appendUInt32(_ value: UInt32) {
        var bigEndian = value.bigEndian
        append(Data(bytes: &bigEndian, count: MemoryLayout<UInt32>.size))
    }

    mutating func appendUInt64(_ value: UInt64) {
        var bigEndian = value.bigEndian
        append(Data(bytes: &bigEndian, count: MemoryLayout<UInt64>.size))
    }
}
