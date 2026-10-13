import Foundation
import Network
import Combine

struct DiscoveredPC: Identifiable, Hashable {
    let id: String
    let name: String
    let address: String
}

@MainActor
final class PCDiscovery: ObservableObject {
    @Published private(set) var computers: [DiscoveredPC] = []
    private var browser: NWBrowser?
    private var resolvers: [String: NWConnection] = [:]
    private var discovered: [String: DiscoveredPC] = [:]

    func start() {
        guard browser == nil else { return }
        let browser = NWBrowser(for: .bonjour(type: "_pcfilebridge._tcp", domain: "local."), using: .tcp)
        browser.stateUpdateHandler = { _ in }
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            Task { @MainActor in self?.update(results) }
        }
        self.browser = browser
        browser.start(queue: .main)
    }

    func stop() {
        browser?.cancel(); browser = nil
        resolvers.values.forEach { $0.cancel() }
        resolvers.removeAll(); discovered.removeAll(); computers = []
    }

    private func update(_ results: Set<NWBrowser.Result>) {
        let services = results.compactMap { result -> (String, String, NWEndpoint)? in
            guard case let .service(name, _, domain, _) = result.endpoint else { return nil }
            return ("\(name).\(domain)", name, result.endpoint)
        }
        let active = Set(services.map(\.0))
        for key in Array(resolvers.keys) where !active.contains(key) { resolvers.removeValue(forKey: key)?.cancel() }
        for key in Array(discovered.keys) where !active.contains(key) { discovered.removeValue(forKey: key) }
        for (key, name, endpoint) in services where resolvers[key] == nil && discovered[key] == nil {
            resolve(key: key, name: name, endpoint: endpoint)
        }
        computers = discovered.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    private func resolve(key: String, name: String, endpoint: NWEndpoint) {
        let parameters = NWParameters.tcp
        let ipOptions = NWProtocolIP.Options()
        ipOptions.version = .v4
        parameters.defaultProtocolStack.internetProtocol = ipOptions
        let connection = NWConnection(to: endpoint, using: parameters)
        resolvers[key] = connection
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let connection else { return }
            switch state {
            case .failed, .cancelled:
                Task { @MainActor in self?.resolvers.removeValue(forKey: key) }
            case .ready:
                guard let endpoint = connection.currentPath?.remoteEndpoint,
                      let address = Self.httpAddress(for: endpoint) else { return }
                Task { @MainActor in
                    guard let self else { return }
                    self.resolvers.removeValue(forKey: key)?.cancel()
                    self.discovered[key] = DiscoveredPC(id: key, name: name, address: address)
                    self.computers = self.discovered.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
                }
            default:
                break
            }
        }
        connection.start(queue: .main)
    }

    nonisolated private static func httpAddress(for endpoint: NWEndpoint) -> String? {
        guard case let .hostPort(host, port) = endpoint else { return nil }
        let hostText: String
        switch host {
        case .ipv4(let address): hostText = "\(address)"
        case .ipv6(let address): hostText = "[\(address)]"
        case .name(let name, _): hostText = name
        @unknown default: return nil
        }
        return "http://\(hostText):\(port.rawValue)"
    }
}
