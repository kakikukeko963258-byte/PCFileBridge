using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PCFileBridge.Windows;

/// <summary>Small DNS-SD responder for _pcfilebridge._tcp.local on IPv4 LANs.</summary>
public sealed class MdnsAdvertiser(int port, string computerName)
{
    private const string Service = "_pcfilebridge._tcp.local";
    private UdpClient? _udp;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public void Start()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            _udp.Ttl = 255;
            foreach (var address in LanAddressProvider.GetIPv4Addresses())
                try { _udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"), address); } catch { }
            _stop = new CancellationTokenSource(); _loop = RunAsync(_stop.Token);
        }
        catch { _udp?.Dispose(); _udp = null; }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (_udp is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = await _udp.ReceiveAsync(ct);
                if (packet.Buffer.Length >= 12) await _udp.SendAsync(BuildResponse(), packet.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private byte[] BuildResponse()
    {
        var host = string.Concat(computerName.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')).Trim('-') + ".local";
        var instance = computerName.Replace('.', '-') + "." + Service;
        var addresses = LanAddressProvider.GetIPv4Addresses();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0x84); writer.Write((byte)0); // response + authoritative
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)(3 + addresses.Length)); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
        WriteRecord(writer, Service, 12, EncodeName(instance), cacheFlush: false);
        using (var srv = new MemoryStream()) using (var sw = new BinaryWriter(srv)) { sw.Write((ushort)0); sw.Write((ushort)0); sw.Write((ushort)IPAddress.HostToNetworkOrder((short)port)); sw.Write(EncodeName(host)); WriteRecord(writer, instance, 33, srv.ToArray()); }
        var label = Encoding.UTF8.GetBytes("v=1"); using (var txt = new MemoryStream()) { txt.WriteByte((byte)label.Length); txt.Write(label); WriteRecord(writer, instance, 16, txt.ToArray()); }
        foreach (var address in addresses) WriteRecord(writer, host, 1, address.GetAddressBytes());
        return stream.ToArray();
    }

    private static void WriteRecord(BinaryWriter writer, string name, ushort type, byte[] data, bool cacheFlush = true)
    {
        var recordClass = cacheFlush ? (ushort)0x8001 : (ushort)1;
        writer.Write(EncodeName(name)); writer.Write((ushort)IPAddress.HostToNetworkOrder((short)type)); writer.Write((ushort)IPAddress.HostToNetworkOrder((short)recordClass)); writer.Write((uint)IPAddress.HostToNetworkOrder(120)); writer.Write((ushort)IPAddress.HostToNetworkOrder((short)data.Length)); writer.Write(data);
    }
    private static byte[] EncodeName(string name)
    {
        using var stream = new MemoryStream(); foreach (var label in name.TrimEnd('.').Split('.')) { var b = Encoding.UTF8.GetBytes(label); stream.WriteByte((byte)b.Length); stream.Write(b); } stream.WriteByte(0); return stream.ToArray();
    }
    public async Task StopAsync() { if (_stop is not null) { _stop.Cancel(); if (_loop is not null) try { await _loop; } catch { } } _udp?.Dispose(); _stop?.Dispose(); }
}
