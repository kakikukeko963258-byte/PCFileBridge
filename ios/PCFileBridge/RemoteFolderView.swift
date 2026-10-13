import SwiftUI

struct RemoteFolderView: View {
    let server: SavedServer
    let parentId: String
    let title: String

    @State private var items: [BridgeItem] = []
    @State private var nextPage: String?
    @State private var isLoading = false
    @State private var errorMessage: String?

    var body: some View {
        List {
            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red)
            }
            ForEach(items) { item in
                if item.isDirectory {
                    NavigationLink {
                        RemoteFolderView(server: server, parentId: item.id, title: item.name)
                    } label: {
                        ItemRow(item: item)
                    }
                } else {
                    ItemRow(item: item)
                }
            }
            if nextPage != nil && nextPage != "" {
                Button(isLoading ? "読み込み中…" : "次のページを読み込む") {
                    Task { await loadNextPage() }
                }.disabled(isLoading)
            } else if items.isEmpty && isLoading {
                ProgressView("読み込み中…")
            } else if items.isEmpty && errorMessage == nil {
                Text("このフォルダーは空です。").foregroundStyle(.secondary)
            }
        }
        .navigationTitle(title)
        .task { await loadNextPage() }
    }

    @MainActor
    private func loadNextPage() async {
        guard !isLoading, nextPage != "" else { return }
        isLoading = true
        defer { isLoading = false }
        do {
            let client = BridgeClient(server: server, token: try SharedTokenStore.load(for: server.id))
            let page = try await client.items(parentId: parentId, continuationToken: nextPage)
            items.append(contentsOf: page.items)
            nextPage = page.next ?? ""
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

private struct ItemRow: View {
    let item: BridgeItem

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: item.isDirectory ? "folder.fill" : "doc")
                .foregroundStyle(item.isDirectory ? .blue : .secondary)
                .frame(width: 24)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.name).lineLimit(1)
                Text(item.isDirectory ? item.modifiedUtc.formatted(date: .abbreviated, time: .shortened) : "\(ByteCountFormatter.string(fromByteCount: item.size, countStyle: .file)) · \(item.modifiedUtc.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
        }
    }
}
