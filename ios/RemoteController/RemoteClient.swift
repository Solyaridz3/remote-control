import Foundation
import Network
import SwiftUI

// All state and Network callbacks run on the main queue. No command can be
// sent before the server's ready message, or after a connection is replaced.
final class RemoteClient: ObservableObject {
    @Published private(set) var connected = false
    @Published private(set) var connecting = false
    @Published private(set) var status = "Нет подключения"
    @Published private(set) var computer = "Windows 11"
    @AppStorage("remote.host") var host = ""
    @AppStorage("remote.port") var port = "49732"
    // Pairing code stays in memory and is never saved to preferences.
    @Published var code = ""
    @AppStorage("remote.sensitivity") var sensitivity = 1.5

    private var connection: NWConnection?
    private var generation = UUID()
    private var buffer = Data()
    private var heartbeat: Timer?
    private var motionTimer: Timer?
    private var lastReply = Date()
    private var pendingX = 0.0
    private var pendingY = 0.0
    private var pendingScroll = 0.0
    private var inFlight = 0

    func connect() {
        disconnect()
        let address = host.trimmingCharacters(in: .whitespacesAndNewlines)
        let pairingCode = code.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard validIPv4(address), let value = UInt16(port), value > 0,
              let endpointPort = NWEndpoint.Port(rawValue: value) else {
            status = "Введите IPv4 адрес ноутбука и порт 1–65535"
            return
        }
        guard pairingCode.count == 12, pairingCode.allSatisfy({ $0.isHexDigit && $0.isASCII }) else {
            status = "Код должен содержать 12 символов: 0–9, A–F"
            return
        }
        host = address
        connecting = true
        status = "Подключение…"
        let id = generation
        let parameters = NWParameters.tls
        if let tcp = parameters.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
            tcp.noDelay = true
        }
        let session = NWConnection(host: NWEndpoint.Host(address), port: endpointPort, using: parameters)
        connection = session
        session.stateUpdateHandler = { [weak self, weak session] state in
            guard let self = self, let session = session, self.generation == id else { return }
            switch state {
            case .ready:
                self.send(["type": "hello", "version": 1, "code": pairingCode], requireReady: false)
                self.receive(session, generation: id)
            case .failed(let error): self.fail("Связь прервана: \(error.localizedDescription)")
            case .waiting: self.status = "Проверьте Wi-Fi, адрес и доступ к локальной сети"
            default: break
            }
        }
        session.start(queue: .main)
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self] in
            guard let self = self, self.generation == id, !self.connected else { return }
            self.fail("Нет ответа. Проверьте Wi-Fi, код и брандмауэр Windows")
        }
    }

    func disconnect() {
        // Closing TCP also releases the server owner. Do not enqueue a final
        // frame here: disconnect must work even when the send queue is full.
        generation = UUID()
        connection?.stateUpdateHandler = nil
        connection?.cancel()
        connection = nil
        heartbeat?.invalidate()
        heartbeat = nil
        motionTimer?.invalidate()
        motionTimer = nil
        connected = false
        connecting = false
        inFlight = 0
        buffer.removeAll()
        pendingX = 0
        pendingY = 0
        pendingScroll = 0
        status = "Нет подключения"
    }

    private func fail(_ message: String) {
        disconnect()
        status = message
    }

    private func validIPv4(_ address: String) -> Bool {
        let parts = address.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count == 4 && parts.allSatisfy {
            !$0.isEmpty && $0.allSatisfy({ $0 >= "0" && $0 <= "9" }) && UInt8($0) != nil
        }
    }

    private func receive(_ session: NWConnection, generation id: UUID) {
        session.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self, weak session] data, _, complete, error in
            guard let self = self, let session = session, self.generation == id else { return }
            if let data = data {
                self.buffer.append(data)
                while let newline = self.buffer.firstIndex(of: 10) {
                    let line = self.buffer.prefix(upTo: newline)
                    guard line.count <= 1024 else { self.fail("Некорректный ответ ноутбука"); return }
                    let frame = Data(line)
                    self.buffer.removeSubrange(...newline)
                    self.handle(frame)
                    guard self.generation == id else { return }
                }
                if self.buffer.count > 1024 { self.fail("Некорректный ответ ноутбука"); return }
            }
            if error != nil || complete { self.fail("Ноутбук отключился"); return }
            self.receive(session, generation: id)
        }
    }

    private func handle(_ data: Data) {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["type"] as? String else { fail("Некорректный ответ ноутбука"); return }
        if type == "error" { fail(object["message"] as? String ?? "Подключение отклонено"); return }
        lastReply = Date()
        if type == "ready" && connecting {
            guard object["version"] as? Int == 1 else { fail("Версии приложений несовместимы"); return }
            connected = true
            connecting = false
            computer = object["name"] as? String ?? "Windows 11"
            status = "Подключено"
            let heartbeatTimer = Timer(timeInterval: 2, repeats: true) { [weak self] _ in
                guard let self = self else { return }
                if Date().timeIntervalSince(self.lastReply) > 7 {
                    self.fail("Связь потеряна. Подключитесь заново")
                } else { self.send(["type": "ping"]) }
            }
            heartbeat = heartbeatTimer
            RunLoop.main.add(heartbeatTimer, forMode: .common)
            let movementTimer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
                self?.flushMotion()
            }
            motionTimer = movementTimer
            RunLoop.main.add(movementTimer, forMode: .common)
        } else if type != "pong" { fail("Неожиданный ответ ноутбука") }
    }

    private func send(_ object: [String: Any], requireReady: Bool = true) {
        guard (!requireReady || connected), let connection = connection,
              var data = try? JSONSerialization.data(withJSONObject: object) else { return }
        // Stop instead of replaying a long queue of stale mouse movements.
        guard inFlight < 16 else { fail("Сеть перегружена. Подключитесь заново"); return }
        data.append(10)
        inFlight += 1
        let id = generation
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self = self, self.generation == id else { return }
            self.inFlight -= 1
            if let error = error { self.fail("Ошибка отправки: \(error.localizedDescription)") }
        })
    }

    func move(dx: CGFloat, dy: CGFloat) {
        guard connected else { return }
        pendingX = max(-500, min(500, pendingX + Double(dx) * sensitivity))
        pendingY = max(-500, min(500, pendingY + Double(dy) * sensitivity))
    }
    func scroll(dy: CGFloat) {
        guard connected else { return }
        pendingScroll = max(-1200, min(1200, pendingScroll + Double(dy) * 5))
    }
    private func flushMotion() {
        guard connected, inFlight < 4 else { return }
        let x = Int(pendingX.rounded(.towardZero)), y = Int(pendingY.rounded(.towardZero))
        if x != 0 || y != 0 {
            pendingX -= Double(x)
            pendingY -= Double(y)
            send(["type": "move", "dx": x, "dy": y])
        }
        let scroll = Int(pendingScroll.rounded(.towardZero))
        if scroll != 0 {
            pendingScroll -= Double(scroll)
            send(["type": "scroll", "delta": scroll])
        }
    }
    func click(right: Bool = false) {
        flushMotion()
        send(["type": "click", "button": right ? "right" : "left"])
    }
    func volume(_ action: String) { send(["type": "volume", "action": action]) }
}
