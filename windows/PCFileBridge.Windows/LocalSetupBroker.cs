using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PCFileBridge.Windows;

// Initial pairing settings are available only to this signed-in Windows user.
// Nothing is exposed through the unauthenticated HTTP health endpoint.
internal static class LocalSetupBroker
{
    private static string PipeName => "PCFileBridge.Setup." + WindowsIdentity.GetCurrent().User!.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string CreateSetup(ServerHost server)
    {
        var address = LanAddressProvider.GetTailscaleAddress() ?? LanAddressProvider.GetIPv4Addresses().FirstOrDefault()
            ?? throw new InvalidOperationException("PCの接続先アドレスが見つかりません。");
        var pairing = server.Store.GetPairingSetup();
        return JsonSerializer.Serialize(new {
            version = 1, setupId = Guid.NewGuid().ToString("N"),
            baseURL = $"http://{address}:{server.Port}", code = pairing.Code,
            expiresUtc = pairing.ExpiresUtc, serverName = Environment.MachineName
        }, Json);
    }

    public static async Task ServeAsync(ServerHost server, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true);
                await writer.WriteLineAsync(CreateSetup(server));
                await writer.FlushAsync(cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch { await Task.Delay(500, cancellation); }
        }
    }

    public static async Task ExportAsync(string output)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var value = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) throw new IOException("接続設定を取得できませんでした。");
        using var parsed = JsonDocument.Parse(value);
        await File.WriteAllTextAsync(Path.GetFullPath(output), value, new UTF8Encoding(false));
    }
}
