using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.StaticFiles;

namespace PCFileBridge.Windows;

public sealed class ServerHost
{
    private readonly WebApplication _app;
    private readonly MdnsAdvertiser _advertiser;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    public BridgeStore Store { get; }
    public int Port => 8765;

    private ServerHost(WebApplication app, MdnsAdvertiser advertiser, BridgeStore store) { _app = app; _advertiser = advertiser; Store = store; }

    public static async Task<ServerHost> StartAsync()
    {
        var storeInstance = new BridgeStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => { o.ListenAnyIP(8765); o.Limits.MaxRequestBodySize = null; });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/pair" && context.Request.Path != "/api/health")
            {
                var auth = context.Request.Headers.Authorization.ToString();
                var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
                if (!storeInstance.Authenticate(token)) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }); return; }
            }
            await next();
        });

        app.MapGet("/api/health", () => Results.Ok(new { status = "online", name = Environment.MachineName, port = 8765, protocolVersion = 1 }));
        app.MapPost("/api/pair", (PairRequest request) =>
        {
            try { var pair = storeInstance.Pair(request.Code, request.DeviceName); return Results.Ok(new PairResponse(pair.DeviceId, pair.Token, Environment.MachineName, 8765)); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        });
        app.MapGet("/api/items", (HttpRequest request) =>
        {
            try
            {
                var parentId = request.Query["parentId"].FirstOrDefault();
                var continuationToken = request.Query["continuationToken"].FirstOrDefault();
                var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 500) : 100;
                var page = storeInstance.List(parentId, continuationToken, limit);
                return Results.Ok(new { items = page.Items, next = page.Next });
            }
            catch (Exception ex) { return Error(ex); }
        });
        app.MapGet("/api/items/{id}", (string id) => { try { return Results.Ok(storeInstance.GetItem(id)); } catch (Exception ex) { return Error(ex); } });
        app.MapGet("/api/changes", (HttpRequest request) =>
        {
            var parent = request.Query["parentId"].FirstOrDefault();
            var itemId = request.Query["itemId"].FirstOrDefault();
            _ = long.TryParse(request.Query["after"], out var after);
            return Results.Ok(storeInstance.GetChanges(parent, Math.Max(0, after), itemId));
        });
        var mime = new FileExtensionContentTypeProvider();
        app.MapGet("/api/items/{id}/content", (string id) =>
        {
            try
            {
                var resolved = storeInstance.ResolveContent(id); var stream = new FileStream(resolved.Item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!mime.TryGetContentType(resolved.Item.Name, out var contentType)) contentType = "application/octet-stream";
                return Results.File(stream, contentType, resolved.Item.Name, resolved.Item.ModifiedUtc, enableRangeProcessing: true);
            }
            catch (Exception ex) { return Error(ex); }
        });
        app.MapPut("/api/items/{id}/content", async (string id, HttpRequest request, CancellationToken ct) =>
        {
            try { await storeInstance.UploadAsync(id, request.Body, ct); return Results.Ok(storeInstance.GetItem(id)); }
            catch (Exception ex) { return Error(ex); }
        });
        app.MapPost("/api/items", (CreateItemRequest request) => { try { var item = storeInstance.Create(request); return Results.Created($"/api/items/{item.Id}", storeInstance.GetItem(item.Id)); } catch (Exception ex) { return Error(ex); } });
        app.MapPatch("/api/items/{id}", (string id, ModifyItemRequest request) => { try { var item = storeInstance.Modify(id, request); return Results.Ok(storeInstance.GetItem(item.Id)); } catch (Exception ex) { return Error(ex); } });
        app.MapDelete("/api/items/{id}", (string id) => { try { storeInstance.Delete(id); return Results.NoContent(); } catch (Exception ex) { return Error(ex); } });
        await app.StartAsync();
        var advertiser = new MdnsAdvertiser(8765, Environment.MachineName);
        advertiser.Start();
        var host = new ServerHost(app, advertiser, storeInstance);
        host.RefreshWatchers();
        storeInstance.RootsChanged += host.RefreshWatchers;
        return host;
    }

    private static IResult Error(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => Results.NotFound(new { error = ex.Message }),
        UnauthorizedAccessException => Results.Json(new { error = ex.Message }, statusCode: 403),
        ArgumentException => Results.BadRequest(new { error = ex.Message }),
        InvalidOperationException => Results.BadRequest(new { error = ex.Message }),
        IOException => Results.Conflict(new { error = ex.Message }),
        _ => Results.Problem(ex.Message)
    };

    private void RefreshWatchers()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        foreach (var root in Store.GetRoots())
        {
            try
            {
                var watcher = new FileSystemWatcher(root.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                watcher.Created += (_, e) => Store.ExternalChanged(e.FullPath, "updated");
                watcher.Changed += (_, e) => Store.ExternalChanged(e.FullPath, "updated");
                watcher.Deleted += (_, e) => Store.ExternalChanged(e.FullPath, "deleted");
                watcher.Renamed += (_, e) => Store.ExternalRenamed(e.OldFullPath, e.FullPath);
                watcher.EnableRaisingEvents = true; _watchers[root.Id] = watcher;
            }
            catch { /* The API still works; status is visible in the desktop app. */ }
        }
    }

    public async Task StopAsync()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        await _advertiser.StopAsync(); await _app.StopAsync(); await _app.DisposeAsync();
    }
}
