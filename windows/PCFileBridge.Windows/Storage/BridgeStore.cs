using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCFileBridge.Windows;

public sealed class BridgeStore
{
    private readonly object _gate = new();
    private readonly string _folder;
    private readonly string _configFile;
    private readonly string _catalogFile;
    private readonly string _devicesFile;
    private readonly string _changesFile;
    private readonly string _writeFoldersFile;
    private readonly List<string> _writableFolders = [];
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private List<SharedRoot> _roots;
    private Dictionary<string, BridgeItem> _items;
    private readonly Dictionary<string, string> _pathIds = new(StringComparer.OrdinalIgnoreCase);
    private List<DeviceRecord> _devices;
    private List<ChangeRecord> _changes;
    private long _sequence;
    private string _pairingCode = NewPairingCode();
    private DateTime _pairingExpires = DateTime.UtcNow.AddMinutes(10);
    private int _pairingFailures;
    private DateTime _pairingBlockedUntil;
    public event Action? RootsChanged;

    public BridgeStore()
    {
        _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCFileBridge");
        Directory.CreateDirectory(_folder);
        _configFile = Path.Combine(_folder, "roots.json"); _catalogFile = Path.Combine(_folder, "catalog.json");
        _devicesFile = Path.Combine(_folder, "devices.json"); _changesFile = Path.Combine(_folder, "changes.json");
        _writeFoldersFile = Path.Combine(_folder, "writable-folders.json");
        foreach (var path in Load<List<string>>(_writeFoldersFile) ?? [])
        {
            try { if (Directory.Exists(path)) _writableFolders.Add(PhysicalPath(path)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _roots = Load<List<SharedRoot>>(_configFile) ?? [];
        _items = (Load<List<BridgeItem>>(_catalogFile) ?? []).ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var item in _items.Values) _pathIds[PathKey(item.Path)] = item.Id;
        _devices = Load<List<DeviceRecord>>(_devicesFile) ?? [];
        _changes = Load<List<ChangeRecord>>(_changesFile) ?? [];
        _sequence = _changes.Count == 0 ? 0 : _changes.Max(c => c.Sequence);
        _roots = _roots.Where(r => Directory.Exists(r.Path)).ToList();
        SaveRoots();
    }

    public string PairingCode { get { lock (_gate) { RotatePairingIfExpired(); return _pairingCode; } } }
    public (string Code, DateTime ExpiresUtc) GetPairingSetup()
    {
        lock (_gate)
        {
            RotatePairingIfExpired();
            if (_pairingExpires < DateTime.UtcNow.AddMinutes(5))
            { _pairingCode = NewPairingCode(); _pairingExpires = DateTime.UtcNow.AddMinutes(10); }
            return (_pairingCode, _pairingExpires);
        }
    }
    public IReadOnlyList<SharedRoot> GetRoots() { lock (_gate) return _roots.Select(Clone).ToArray(); }
    public IReadOnlyList<string> GetWritableFolders() { lock (_gate) return _writableFolders.ToArray(); }

    public void AllowWrites(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("フォルダーが見つかりません。");
        var full = PhysicalPath(path);
        lock (_gate)
        {
            if (!_roots.Any(r => IsWithin(full, r.Path))) throw new InvalidOperationException("先にこのフォルダーを含むドライブを表示対象にしてください。");
            if (_writableFolders.Any(p => IsWithin(full, p))) return;
            _writableFolders.RemoveAll(p => IsWithin(p, full));
            _writableFolders.Add(full);
            Save(_writeFoldersFile, _writableFolders);
            PermissionsChanged();
        }
    }

    public void ClearWritePermissions()
    {
        lock (_gate)
        {
            _writableFolders.Clear();
            Save(_writeFoldersFile, _writableFolders);
            PermissionsChanged();
        }
    }

    private void PermissionsChanged()
    {
        foreach (var item in _items.Values)
        {
            var root = _roots.FirstOrDefault(r => r.Id == item.RootId);
            var readOnly = root is null || !CanWrite(item.Path, root);
            if (item.ReadOnly == readOnly) continue;
            item.ReadOnly = readOnly;
            _changes.Add(new ChangeRecord(++_sequence, item.ParentId, item.Id, "updated"));
        }
        if (_changes.Count > 20000) _changes.RemoveRange(0, _changes.Count - 20000);
        SaveCatalog(); SaveChanges();
    }
    public void AddAllDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var path = drive.RootDirectory.FullName;
                lock (_gate)
                {
                    if (_roots.Any(r => SamePath(r.Path, path))) continue;
                    // A drive replaces nested shares so each path has one stable owner.
                    foreach (var nested in _roots.Where(r => IsWithin(r.Path, path)).ToArray()) RemoveRoot(nested.Id);
                    var root = new SharedRoot { Id = "root-" + Guid.NewGuid().ToString("N"), Name = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})", Path = path, ReadOnly = true };
                    _roots.Add(root);
                    UpsertPath(path, root.Id, "root", root);
                    SaveRoots(); SaveCatalog(); Record("root", root.Id, "updated");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        RootsChanged?.Invoke();
    }
    public IReadOnlyList<DeviceRecord> GetDevices() { lock (_gate) return _devices.Select(d => new DeviceRecord { Id = d.Id, Name = d.Name, TokenHash = "", LastAccessUtc = d.LastAccessUtc }).ToArray(); }

    public SharedRoot AddRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("選択したフォルダーが存在しません。");
        try { full = Path.TrimEndingDirectorySeparator(new DirectoryInfo(full).ResolveLinkTarget(true)?.FullName ?? full); } catch { }
        lock (_gate)
        {
            if (_roots.Any(r => SamePath(r.Path, full))) throw new InvalidOperationException("このフォルダーはすでに公開されています。");
            if (_roots.Any(r => IsWithin(full, r.Path) || IsWithin(r.Path, full))) throw new InvalidOperationException("公開フォルダー同士を重ねて登録することはできません。");
            var root = new SharedRoot { Id = "root-" + Guid.NewGuid().ToString("N"), Name = Path.GetFileName(full) is { Length: > 0 } name ? name : full, Path = full };
            _roots.Add(root); SaveRoots();
            UpsertPath(root.Path, root.Id, "root", root); SaveCatalog(); Record("root", root.Id, "updated");
            RootsChanged?.Invoke();
            return Clone(root);
        }
    }

    public void RemoveRoot(string id)
    {
        lock (_gate)
        {
            var root = _roots.FirstOrDefault(r => r.Id == id);
            if (root is null) return;
            Record("root", root.Id, "deleted");
            _roots.Remove(root);
            foreach (var item in _items.Values.Where(i => i.RootId == id).ToArray()) { _items.Remove(item.Id); _pathIds.Remove(PathKey(item.Path)); }
            SaveRoots(); SaveCatalog(); SaveChanges();
            RootsChanged?.Invoke();
        }
    }

    public (string DeviceId, string Token) Pair(string code, string deviceName)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow < _pairingBlockedUntil) throw new UnauthorizedAccessException("試行回数が多いため、しばらくしてから再試行してください。");
            RotatePairingIfExpired();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code ?? ""), Encoding.UTF8.GetBytes(_pairingCode)) || DateTime.UtcNow >= _pairingExpires)
            {
                if (++_pairingFailures >= 5) { _pairingBlockedUntil = DateTime.UtcNow.AddMinutes(1); _pairingFailures = 0; }
                throw new UnauthorizedAccessException("ペアリングコードが正しくないか、期限切れです。");
            }
            var tokenBytes = RandomNumberGenerator.GetBytes(48);
            var token = Base64Url(tokenBytes);
            var device = new DeviceRecord { Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(deviceName) ? "iPhone" : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 64)], TokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))) };
            _devices.Add(device); SaveDevices();
            _pairingCode = NewPairingCode(); _pairingExpires = DateTime.UtcNow.AddMinutes(10);
            _pairingFailures = 0;
            return (device.Id, token);
        }
    }

    public bool Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        lock (_gate)
        {
            var match = _devices.FirstOrDefault(d => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(d.TokenHash), Encoding.UTF8.GetBytes(hash)));
            if (match is null) return false;
            match.LastAccessUtc = DateTime.UtcNow; SaveDevices(); return true;
        }
    }

    public (IReadOnlyList<ItemDto> Items, string? Next) List(string? parentId, string? continuationToken, int limit)
    {
        lock (_gate)
        {
            var cursor = DecodePageToken(continuationToken);
            if (string.IsNullOrEmpty(parentId) || parentId == "root")
            {
                var roots = _roots.Select(r => ToDto(UpsertPath(r.Path, r.Id, "root", r)))
                    .Where(i => IsAfter(i.Name, i.Id, cursor))
                    .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id, StringComparer.Ordinal)
                    .Take(limit + 1).ToList();
                return Page(roots, limit);
            }
            var folder = Resolve(parentId);
            if (!folder.IsDirectory) throw new InvalidOperationException("ファイルの中身は一覧表示できません。");
            EnsureAllowed(folder.Path, _roots.Single(r => r.Id == folder.RootId));
            var comparer = Comparer<ItemDto>.Create((a, b) => { var cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Id, b.Id); });
            var entries = new SortedSet<ItemDto>(comparer);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder.Path))
            {
                if (Path.GetFileName(path).StartsWith(".pcfb-", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    EnsureAllowed(path, _roots.Single(r => r.Id == folder.RootId));
                    var item = ToDto(UpsertPath(path, folder.RootId, folder.Id, null));
                    if (IsAfter(item.Name, item.Id, cursor))
                    {
                        entries.Add(item);
                        if (entries.Count > limit + 1) entries.Remove(entries.Max!);
                    }
                }
                catch (FileNotFoundException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            SaveCatalog();
            return Page(entries.ToList(), limit);
        }
    }

    public ItemDto GetItem(string id) { lock (_gate) return ToDto(Resolve(id)); }

    public (BridgeItem Item, SharedRoot Root) ResolveContent(string id)
    {
        lock (_gate)
        {
            var item = Resolve(id);
            if (item.IsDirectory) throw new InvalidOperationException("フォルダーにファイル内容はありません。");
            var root = _roots.Single(r => r.Id == item.RootId); EnsureAllowed(item.Path, root);
            return (item, Clone(root));
        }
    }

    public BridgeItem Create(CreateItemRequest request)
    {
        ValidateName(request.Name);
        lock (_gate)
        {
            var parentPath = GetDestinationParent(request.ParentId, out var root, out var parentId);
            var path = Path.Combine(parentPath, request.Name);
            EnsureAllowed(path, root);
            EnsureWritable(path, root);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("同じ名前の項目がすでにあります。");
            if (request.IsDirectory) Directory.CreateDirectory(path); else using (File.Create(path)) { }
            var item = UpsertPath(path, root.Id, parentId, null); Record(parentId, item.Id, "updated"); SaveCatalog();
            return Copy(item);
        }
    }

    public BridgeItem Modify(string id, ModifyItemRequest request)
    {
        lock (_gate)
        {
            var item = Resolve(id); var root = _roots.Single(r => r.Id == item.RootId); EnsureAllowed(item.Path, root);
            EnsureWritable(item.Path, root);
            var oldParent = item.ParentId; var oldPath = item.Path;
            var targetParentPath = request.ParentId is null ? Path.GetDirectoryName(oldPath)! : GetDestinationParent(request.ParentId, out root, out var ignoredParentId);
            var targetParentId = request.ParentId ?? oldParent;
            if (request.Name is not null) ValidateName(request.Name);
            var targetPath = Path.Combine(targetParentPath, request.Name ?? Path.GetFileName(oldPath));
            EnsureAllowed(targetPath, root);
            EnsureWritable(targetPath, root);
            if (!SamePath(oldPath, targetPath)) EnsurePermissionBoundaryUnchanged(oldPath);
            if (item.IsDirectory && IsWithin(targetParentPath, oldPath)) throw new InvalidOperationException("フォルダーを自分自身の中へ移動できません。");
            if (!SamePath(oldPath, targetPath) && (File.Exists(targetPath) || Directory.Exists(targetPath))) throw new IOException("移動先に同じ名前の項目があります。");
            if (!SamePath(oldPath, targetPath))
            {
                MovePath(oldPath, targetPath, item.IsDirectory);
                var moved = _items.Values.Where(i => SamePath(i.Path, oldPath) || IsWithin(i.Path, oldPath)).ToArray();
                foreach (var child in moved)
                {
                    var suffix = child.Path.Length == oldPath.Length ? "" : child.Path[oldPath.Length..];
                    _pathIds.Remove(PathKey(child.Path));
                    child.Path = targetPath + suffix;
                    _pathIds[PathKey(child.Path)] = child.Id;
                    child.RootId = root.Id;
                    if (child.Id == id) { child.ParentId = targetParentId; child.Name = Path.GetFileName(targetPath); }
                }
                if (oldParent != targetParentId) Record(oldParent, id, "deleted");
                RefreshMetadata(item); Record(targetParentId, id, "updated"); SaveCatalog();
            }
            else { RefreshMetadata(item); SaveCatalog(); }
            return Copy(item);
        }
    }

    public async Task UploadAsync(string id, Stream input, CancellationToken cancellationToken)
    {
        BridgeItem item; SharedRoot root;
        lock (_gate) { item = Resolve(id); root = Clone(_roots.Single(r => r.Id == item.RootId)); EnsureAllowed(item.Path, root); EnsureWritable(item.Path, root); if (item.IsDirectory) throw new InvalidOperationException("フォルダーへアップロードできません。"); }
        var temp = Path.Combine(Path.GetDirectoryName(item.Path)!, ".pcfb-" + Guid.NewGuid().ToString("N") + ".upload");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
            lock (_gate) { EnsureAllowed(item.Path, root); EnsureWritable(item.Path, root); File.Move(temp, item.Path, true); RefreshMetadata(item); Record(item.ParentId, id, "updated"); SaveCatalog(); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var item = Resolve(id); var root = _roots.Single(r => r.Id == item.RootId); EnsureAllowed(item.Path, root);
            EnsureWritable(item.Path, root);
            EnsurePermissionBoundaryUnchanged(item.Path);
            if (SamePath(item.Path, root.Path)) throw new UnauthorizedAccessException("公開元フォルダーそのものは削除できません。");
            if (item.IsDirectory) Directory.Delete(item.Path, true); else File.Delete(item.Path);
            var ids = _items.Values.Where(i => i.Id == id || IsWithin(i.Path, item.Path)).Select(i => i.Id).ToArray();
            foreach (var stale in ids) { if (_items.Remove(stale, out var removed)) _pathIds.Remove(PathKey(removed.Path)); }
            Record(item.ParentId, id, "deleted"); SaveCatalog();
        }
    }

    public ChangePage GetChanges(string? parentId, long after, string? itemId = null)
    {
        lock (_gate)
        {
            var current = _sequence;
            var matching = _changes.Where(c => c.Sequence > after &&
                (itemId is not null ? c.ItemId == itemId : parentId is not null ? c.ParentId == parentId : true)).Take(2001).ToArray();
            var more = matching.Length > 2000;
            var page = matching.Take(2000).ToArray();
            return new ChangePage(page, more ? page[^1].Sequence : current);
        }
    }

    public ItemDto? TryGetItem(string id) { lock (_gate) { try { return ToDto(Resolve(id)); } catch { return null; } } }

    public void ExternalChanged(string fullPath, string kind)
    {
        lock (_gate)
        {
            if (IsWithin(fullPath, _folder)) return;
            if (Path.GetFileName(fullPath).StartsWith(".pcfb-", StringComparison.OrdinalIgnoreCase)) return;
            var root = _roots.FirstOrDefault(r => IsWithin(fullPath, r.Path)); if (root is null || SamePath(fullPath, root.Path)) return;
            var parentPath = Path.GetDirectoryName(fullPath); if (parentPath is null) return;
            var parent = _pathIds.TryGetValue(PathKey(parentPath), out var parentId) ? _items.GetValueOrDefault(parentId) : null;
            if (parent is null) return;
            if (kind == "deleted")
            {
                var old = _pathIds.TryGetValue(PathKey(fullPath), out var oldId) ? _items.GetValueOrDefault(oldId) : null;
                if (old is not null) { Record(parent.Id, old.Id, "deleted"); foreach (var stale in _items.Values.Where(i => i.Id == old.Id || IsWithin(i.Path, old.Path)).Select(i => i.Id).ToArray()) { if (_items.Remove(stale, out var removed)) _pathIds.Remove(PathKey(removed.Path)); } SaveCatalog(); }
            }
            else if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                var item = UpsertPath(fullPath, root.Id, parent.Id, null); Record(parent.Id, item.Id, "updated"); SaveCatalog();
            }
        }
    }

    public void ExternalRenamed(string oldPath, string newPath)
    {
        lock (_gate)
        {
            if (IsWithin(oldPath, _folder) || IsWithin(newPath, _folder)) return;
            var item = _pathIds.TryGetValue(PathKey(oldPath), out var id) ? _items.GetValueOrDefault(id) : null;
            var oldParent = item?.ParentId;
            var root = _roots.FirstOrDefault(r => IsWithin(newPath, r.Path));
            if (item is null || root is null)
            {
                if (item is not null && oldParent is not null) { Record(oldParent, item.Id, "deleted"); _items.Remove(item.Id); _pathIds.Remove(PathKey(item.Path)); SaveCatalog(); }
                return;
            }
            var newParentPath = Path.GetDirectoryName(newPath)!;
            var newParent = _pathIds.TryGetValue(PathKey(newParentPath), out var newParentIdFromPath) ? _items.GetValueOrDefault(newParentIdFromPath) : null;
            var newParentId = newParent?.Id ?? oldParent!;
            var oldFull = Path.GetFullPath(oldPath); var newFull = Path.GetFullPath(newPath);
            foreach (var child in _items.Values.Where(i => SamePath(i.Path, oldFull) || IsWithin(i.Path, oldFull)).ToArray())
            {
                _pathIds.Remove(PathKey(child.Path));
                child.Path = newFull + (child.Path.Length == oldFull.Length ? "" : child.Path[oldFull.Length..]);
                _pathIds[PathKey(child.Path)] = child.Id;
                child.RootId = root.Id;
                if (child.Id == item.Id) { child.Name = Path.GetFileName(newFull); child.ParentId = newParentId; }
            }
            if (oldParent != newParentId) Record(oldParent!, item.Id, "deleted");
            RefreshMetadata(item); Record(newParentId, item.Id, "updated"); SaveCatalog();
        }
    }

    private BridgeItem UpsertPath(string path, string rootId, string parentId, SharedRoot? root)
    {
        var full = Path.GetFullPath(path); var existing = _pathIds.TryGetValue(PathKey(full), out var existingId) ? _items.GetValueOrDefault(existingId) : null;
        var isDirectory = Directory.Exists(full); var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists && !SamePath(full, root?.Path ?? "")) throw new FileNotFoundException("項目が見つかりません。", full);
        var entry = existing ?? new BridgeItem { Id = "item-" + Guid.NewGuid().ToString("N"), ParentId = parentId, RootId = rootId, Name = "", Path = full, IsDirectory = isDirectory, Version = "" };
        entry.ParentId = parentId; entry.RootId = rootId; entry.Name = root?.Name ?? Path.GetFileName(full); entry.Path = full; entry.IsDirectory = isDirectory;
        var owner = root ?? _roots.FirstOrDefault(r => r.Id == rootId);
        entry.ReadOnly = owner is null || !CanWrite(full, owner);
        entry.Size = isDirectory ? 0 : new FileInfo(full).Length; entry.ModifiedUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow;
        entry.Version = $"{entry.ModifiedUtc.Ticks:x}-{entry.Size:x}";
        _items[entry.Id] = entry; _pathIds[PathKey(full)] = entry.Id; return entry;
    }

    private BridgeItem Resolve(string id)
    {
        var item = _items.GetValueOrDefault(id) ?? throw new FileNotFoundException("項目が見つかりません。");
        var root = _roots.FirstOrDefault(r => r.Id == item.RootId) ?? throw new FileNotFoundException("公開フォルダーが見つかりません。");
        EnsureAllowed(item.Path, root);
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) throw new FileNotFoundException("項目が見つかりません。");
        RefreshMetadata(item); return item;
    }

    private string GetDestinationParent(string parentId, out SharedRoot root, out string actualParentId)
    {
        if (parentId == "root" || string.IsNullOrEmpty(parentId)) throw new InvalidOperationException("公開フォルダー直下への作成は、公開フォルダーIDを指定してください。");
        if (_roots.FirstOrDefault(r => r.Id == parentId) is { } sharedRoot) { root = sharedRoot; actualParentId = sharedRoot.Id; return sharedRoot.Path; }
        var parent = Resolve(parentId); if (!parent.IsDirectory) throw new InvalidOperationException("親項目はフォルダーではありません。");
        root = _roots.Single(r => r.Id == parent.RootId); actualParentId = parent.Id; return parent.Path;
    }

    private void RefreshMetadata(BridgeItem item)
    {
        var root = _roots.FirstOrDefault(r => r.Id == item.RootId);
        item.ReadOnly = root is null || !CanWrite(item.Path, root);
        var info = item.IsDirectory ? (FileSystemInfo)new DirectoryInfo(item.Path) : new FileInfo(item.Path);
        if (info.Exists) { item.ModifiedUtc = info.LastWriteTimeUtc; item.Size = item.IsDirectory ? 0 : new FileInfo(item.Path).Length; item.Version = $"{item.ModifiedUtc.Ticks:x}-{item.Size:x}"; }
    }
    private void Record(string parentId, string itemId, string kind) { _changes.Add(new ChangeRecord(++_sequence, parentId, itemId, kind)); if (_changes.Count > 20000) _changes.RemoveRange(0, _changes.Count - 20000); SaveChanges(); }
    private void RotatePairingIfExpired() { if (DateTime.UtcNow >= _pairingExpires) { _pairingCode = NewPairingCode(); _pairingExpires = DateTime.UtcNow.AddMinutes(10); } }
    private static string NewPairingCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string root)
    {
        var p = Path.GetFullPath(path); var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static void EnsureAllowed(string path, SharedRoot root)
    {
        if (!IsWithin(path, root.Path)) throw new UnauthorizedAccessException("公開フォルダーの外にはアクセスできません。");
        var relative = Path.GetRelativePath(root.Path, Path.GetFullPath(path));
        if (relative == ".") return;
        var current = root.Path;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); } catch { continue; }
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(true)?.FullName;
            if (target is null || !IsWithin(target, root.Path)) throw new UnauthorizedAccessException("公開フォルダー外を指すリンクにはアクセスできません。");
            current = target;
        }
    }
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')) throw new ArgumentException("ファイル名が正しくありません。"); }
    private static void MovePath(string source, string destination, bool isDirectory)
    {
        try { if (isDirectory) Directory.Move(source, destination); else File.Move(source, destination); return; }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase)) { }
        if (!isDirectory)
        {
            File.Copy(source, destination, false); File.Delete(source); return;
        }
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source)) MovePath(directory, Path.Combine(destination, Path.GetFileName(directory)), true);
        foreach (var file in Directory.EnumerateFiles(source)) { File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false); File.Delete(file); }
        Directory.Delete(source);
    }
    private static (IReadOnlyList<ItemDto> Items, string? Next) Page(List<ItemDto> all, int limit)
    {
        var hasMore = all.Count > limit; if (hasMore) all.RemoveAt(limit);
        return (all, hasMore ? EncodePageToken(all[^1].Name, all[^1].Id) : null);
    }
    private readonly record struct PageCursor(string Name, string Id);
    private static bool IsAfter(string name, string id, PageCursor? cursor)
    {
        if (cursor is null) return true;
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(name, cursor.Value.Name);
        return comparison > 0 || (comparison == 0 && StringComparer.Ordinal.Compare(id, cursor.Value.Id) > 0);
    }
    private static PageCursor? DecodePageToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/'); normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split('\n', 2);
            return new PageCursor(parts[0], parts.Length > 1 ? parts[1] : "");
        }
        catch { throw new ArgumentException("継続トークンが正しくありません。"); }
    }
    private static string EncodePageToken(string name, string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(name + "\n" + id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string PathKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static ItemDto ToDto(BridgeItem i) => new(i.Id, i.ParentId, i.Name, i.IsDirectory, i.Size, i.ModifiedUtc, i.Version, i.ReadOnly);
    private static BridgeItem Copy(BridgeItem i) => new() { Id = i.Id, ParentId = i.ParentId, RootId = i.RootId, Name = i.Name, Path = i.Path, IsDirectory = i.IsDirectory, Size = i.Size, ModifiedUtc = i.ModifiedUtc, Version = i.Version, ReadOnly = i.ReadOnly };
    private bool CanWrite(string path, SharedRoot root)
    {
        if (!root.ReadOnly) return true;
        try
        {
            var physical = PhysicalPath(path);
            return _writableFolders.Any(folder => IsWithin(path, folder) && IsWithin(physical, folder));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private void EnsureWritable(string path, SharedRoot root)
    {
        if (!CanWrite(path, root)) throw new UnauthorizedAccessException("この場所は読み取り専用です。PCで許可したフォルダーの中に保存してください。");
    }
    private void EnsurePermissionBoundaryUnchanged(string path)
    {
        if (_writableFolders.Any(folder => SamePath(path, folder))) throw new UnauthorizedAccessException("書き込み許可の起点フォルダー自体は移動・削除できません。");
    }
    private static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("パスが正しくありません。");
        var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("リンク先を解決できません。");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    private static SharedRoot Clone(SharedRoot r) => new() { Id = r.Id, Name = r.Name, Path = r.Path, ReadOnly = r.ReadOnly };
    private T? Load<T>(string path) { try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), _json) : default; } catch { return default; } }
    private void SaveRoots() => Save(_configFile, _roots);
    private void SaveCatalog() => Save(_catalogFile, _items.Values.ToList());
    private void SaveDevices() => Save(_devicesFile, _devices);
    private void SaveChanges() => Save(_changesFile, _changes);
    private void Save<T>(string path, T value) { var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, _json)); File.Move(temp, path, true); }
}
