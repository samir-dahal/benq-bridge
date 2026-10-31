using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

namespace BenqBridge;

/// <summary>What the pairing page needs: the URLs a phone can reach and a QR code for each.</summary>
internal static class Pairing
{
    private static readonly string[] VirtualAdapterHints =
        { "vethernet", "vmware", "virtualbox", "hyper-v", "wsl", "docker" };

    public sealed record LanAddress(string Ip, string Adapter, bool Likely);

    public sealed record PairAddress(string Url, string Adapter, string Svg);

    /// <summary>
    /// IPv4 addresses of connected adapters, best guess first: a real adapter with a default
    /// gateway beats a virtual one (WSL, Hyper-V, VMware) that a phone could never reach.
    /// </summary>
    public static IReadOnlyList<LanAddress> FindLanAddresses()
    {
        var found = new List<LanAddress>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var isVirtual = VirtualAdapterHints.Any(hint =>
                    (nic.Name + " " + nic.Description).Contains(hint, StringComparison.OrdinalIgnoreCase));

                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address.Address) || IsLinkLocal(address.Address)) continue;

                    found.Add(new LanAddress(address.Address.ToString(), nic.Name, hasGateway && !isVirtual));
                }
            }
        }
        catch
        {
            // best effort only
        }

        return found.OrderByDescending(a => a.Likely).ToList();
    }

    /// <summary>One entry per reachable address; the QR code opens the UI and hands over the token.</summary>
    public static IReadOnlyList<PairAddress> Build(int port, string token)
    {
        var result = new List<PairAddress>();

        foreach (var lan in FindLanAddresses())
        {
            var url = $"http://{lan.Ip}:{port}/";

            // The token rides in the URL fragment: browsers never send it to the server,
            // and the UI strips it from the address bar as soon as it has stored it.
            var target = $"{url}#t={Uri.EscapeDataString(token)}";

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(target, QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data).GetGraphic(
                8, "#111111", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);

            result.Add(new PairAddress(url, lan.Adapter, svg));
        }

        return result;
    }

    /// <summary>
    /// The pairing data contains the token, so only the PC itself may ask for it. The Host
    /// check stops a web page from reading it through DNS rebinding.
    /// </summary>
    public static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return false;

        return context.Request.Host.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
