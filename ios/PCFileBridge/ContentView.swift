import SwiftUI

struct ContentView: View {
    @StateObject private var model = BridgeViewModel()
    @StateObject private var discovery = PCDiscovery()
    @StateObject private var changeMonitor = ChangeMonitor()
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        NavigationStack {
            Form {
                Section("Windows PCとペアリング") {
                    TextField("PCのアドレス 例: 192.168.1.20:8765", text: $model.address)
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                    TextField("PC画面の6桁コード", text: $model.pairingCode)
                        .keyboardType(.numberPad).onChange(of: model.pairingCode) { value in model.pairingCode = String(value.filter(\.isNumber).prefix(6)) }
                    Button { Task { await model.pair() } } label: {
                        if model.isWorking { ProgressView().frame(maxWidth: .infinity) }
                        else { Text("ペアリングしてFilesに追加").frame(maxWidth: .infinity) }
                    }.disabled(model.isWorking)
                }

                Section {
                    if discovery.computers.isEmpty {
                        Text("同じWi-Fi上のPCを検索しています…").foregroundStyle(.secondary)
                    } else {
                        ForEach(discovery.computers) { pc in
                            Button {
                                model.address = pc.address
                            } label: {
                                HStack { VStack(alignment: .leading) { Text(pc.name); Text(pc.address).font(.caption).foregroundStyle(.secondary) }; Spacer(); Image(systemName: "arrow.up.left") }
                            }.buttonStyle(.plain)
                        }
                    }
                } header: { Text("ネットワーク上で見つかったPC") }

                Section("Filesに登録済み") {
                    if model.servers.isEmpty { Text("ペアリングしたPCはありません。").foregroundStyle(.secondary) }
                    ForEach(model.servers) { server in
                        VStack(alignment: .leading, spacing: 8) {
                            Text(server.name).font(.headline)
                            Text(server.baseURL).font(.caption).foregroundStyle(.secondary)
                            NavigationLink("ファイルを閲覧") {
                                RemoteFolderView(server: server, parentId: "root", title: server.name)
                            }
                            HStack {
                                Button("接続テスト") { Task { await model.test(server) } }
                                Spacer()
                                Button("Filesから削除", role: .destructive) { Task { await model.remove(server) } }
                            }.font(.subheadline)
                        }.padding(.vertical, 4)
                    }
                }

                if let message = model.message { Section { Label(message, systemImage: "checkmark.circle.fill").foregroundStyle(.green) } }
                if let error = model.error { Section { Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.red) } }
                Section {
                    Text("PC File Bridgeは選択したフォルダーだけを共有します。接続後、Filesアプリの「ブラウズ」→「場所」にPC名が表示されます。")
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
            .navigationTitle("PC File Bridge")
            .task {
                discovery.start()
                if scenePhase == .active { changeMonitor.start(servers: model.servers) }
            }
            .onChange(of: model.servers) { servers in
                if scenePhase == .active { changeMonitor.start(servers: servers) }
            }
            .onChange(of: scenePhase) { phase in
                if phase == .active { changeMonitor.start(servers: model.servers) }
                else { changeMonitor.stop() }
            }
            .onDisappear { discovery.stop(); changeMonitor.stop() }
        }
    }
}
