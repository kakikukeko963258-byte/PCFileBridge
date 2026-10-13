using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCFileBridge.Windows;

internal static class LanAddressProvider
{
    public static IReadOnlyList<IPAddress> GetIPv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                              network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Select(network => new
            {
                Network = network,
                Properties = network.GetIPProperties()
            })
            .Where(item => item.Properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any)))
            .OrderBy(item => item.Network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .SelectMany(item => item.Properties.UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                              !IPAddress.IsLoopback(address) &&
                              !address.IsIPv6LinkLocal &&
                              !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()
            .ToArray();
    }
}
