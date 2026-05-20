import Foundation
import Network

struct StreamHello: Encodable {
    let version: Int
    let codec: String
    let width: Int
    let height: Int
    let fps: Int
    let orientation: String
    let framing: String
}

final class StreamServer {
    static let port: UInt16 = 48650

    var onStatusChange: ((String) -> Void)?

    private let queue = DispatchQueue(label: "windowsCam.stream.server")
    private var listener: NWListener?
    private var clients: [StreamClient] = []
    private var latestHello = StreamHello(
        version: 1,
        codec: "h264-annexb",
        width: 3840,
        height: 2160,
        fps: 30,
        orientation: "landscapeRight",
        framing: "uint32be-length-prefixed"
    )

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
        }
    }

    func updateHello(width: Int, height: Int, fps: Int) {
        queue.async {
            self.latestHello = StreamHello(
                version: 1,
                codec: "h264-annexb",
                width: width,
                height: height,
                fps: fps,
                orientation: "landscapeRight",
                framing: "uint32be-length-prefixed"
            )
        }
    }

    func broadcast(_ frame: Data) {
        queue.async {
            guard !self.clients.isEmpty else { return }
            let packet = Self.packet(frame)
            let isKeyFrame = Self.isH264KeyFrame(frame)

            self.clients.forEach { client in
                self.queuePacket(
                    StreamPacket(data: packet, isDroppable: true, isKeyFrame: isKeyFrame),
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
        guard let data = try? JSONEncoder().encode(latestHello) else { return }
        queuePacket(
            StreamPacket(data: Self.packet(data), isDroppable: false, isKeyFrame: true),
            to: client
        )
    }

    private func queuePacket(_ packet: StreamPacket, to client: StreamClient) {
        if packet.isDroppable, client.needsKeyFrame {
            guard packet.isKeyFrame else { return }
            client.needsKeyFrame = false
        }

        if client.isSending {
            if packet.isDroppable, client.pendingPacket?.isDroppable == true {
                client.needsKeyFrame = true
                guard packet.isKeyFrame else { return }
                client.needsKeyFrame = false
            }

            client.pendingPacket = packet
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

    private static func isH264KeyFrame(_ frame: Data) -> Bool {
        frame.withUnsafeBytes { rawBuffer in
            let bytes = rawBuffer.bindMemory(to: UInt8.self)
            var index = 0

            while index + 3 < bytes.count {
                let nalOffset: Int?
                if bytes[index] == 0, bytes[index + 1] == 0, bytes[index + 2] == 1 {
                    nalOffset = index + 3
                } else if index + 4 < bytes.count,
                          bytes[index] == 0,
                          bytes[index + 1] == 0,
                          bytes[index + 2] == 0,
                          bytes[index + 3] == 1 {
                    nalOffset = index + 4
                } else {
                    index += 1
                    continue
                }

                guard let nalOffset, nalOffset < bytes.count else { return false }
                if bytes[nalOffset] & 0x1F == 5 {
                    return true
                }

                index = nalOffset + 1
            }

            return false
        }
    }
}

private final class StreamClient {
    let connection: NWConnection
    var isSending = false
    var needsKeyFrame = false
    var pendingPacket: StreamPacket?

    init(connection: NWConnection) {
        self.connection = connection
    }
}

private struct StreamPacket {
    let data: Data
    let isDroppable: Bool
    let isKeyFrame: Bool
}
