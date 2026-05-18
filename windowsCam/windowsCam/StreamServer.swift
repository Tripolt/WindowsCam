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
    private var connections: [NWConnection] = []
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
                let parameters = NWParameters.tcp
                parameters.allowLocalEndpointReuse = true
                let listener = try NWListener(using: parameters, on: NWEndpoint.Port(rawValue: Self.port)!)
                self.listener = listener

                listener.stateUpdateHandler = { [weak self] state in
                    switch state {
                    case .ready:
                        self?.emit("USB port \(Self.port)")
                    case .failed(let error):
                        self?.emit("Stream failed: \(error.localizedDescription)")
                        self?.stop()
                    case .cancelled:
                        self?.emit("Stream stopped")
                    default:
                        break
                    }
                }

                listener.newConnectionHandler = { [weak self] connection in
                    self?.accept(connection)
                }

                listener.start(queue: self.queue)
            } catch {
                self.emit("Stream error: \(error.localizedDescription)")
            }
        }
    }

    func stop() {
        queue.async {
            self.listener?.cancel()
            self.listener = nil
            self.connections.forEach { $0.cancel() }
            self.connections.removeAll()
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
            guard !self.connections.isEmpty else { return }
            let packet = Self.packet(frame)
            self.connections.forEach { connection in
                connection.send(content: packet, completion: .contentProcessed { _ in })
            }
        }
    }

    private func accept(_ connection: NWConnection) {
        connections.append(connection)

        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection else { return }

            if case .ready = state {
                self.sendHello(on: connection)
                self.emit("\(self.connections.count) OBS client(s)")
            }

            if case .failed = state {
                self.remove(connection)
            }

            if case .cancelled = state {
                self.remove(connection)
            }
        }

        connection.start(queue: queue)
    }

    private func sendHello(on connection: NWConnection) {
        guard let data = try? JSONEncoder().encode(latestHello) else { return }
        connection.send(content: Self.packet(data), completion: .contentProcessed { _ in })
    }

    private func remove(_ connection: NWConnection) {
        connections.removeAll { $0 === connection }
        emit(connections.isEmpty ? "USB port \(Self.port)" : "\(connections.count) OBS client(s)")
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
}
