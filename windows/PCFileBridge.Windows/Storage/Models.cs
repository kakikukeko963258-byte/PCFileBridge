using System.Text.Json.Serialization;

namespace PCFileBridge.Windows;

public sealed class SharedRoot
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool ReadOnly { get; set; }
    [JsonIgnore] public string DisplayLabel => $"{Name}    {Path}";
}

public sealed class BridgeItem
{
    public required string Id { get; set; }
    public required string ParentId { get; set; }
    public required string RootId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public required string Version { get; set; }
}

public sealed record ItemDto(string Id, string ParentId, string Name, bool IsDirectory, long Size, DateTime ModifiedUtc, string Version);
public sealed record PairRequest(string Code, string DeviceName);
public sealed record PairResponse(string DeviceId, string Token, string ServerName, int Port);
public sealed record CreateItemRequest(string ParentId, string Name, bool IsDirectory);
public sealed record ModifyItemRequest(string? Name, string? ParentId);
public sealed record ChangeRecord(long Sequence, string ParentId, string ItemId, string Kind);
public sealed record ChangePage(IReadOnlyList<ChangeRecord> Changes, long LatestSequence);
public sealed class DeviceRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public DateTime? LastAccessUtc { get; set; }
    [JsonIgnore] public string LastAccessLocal => LastAccessUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未接続";
}
