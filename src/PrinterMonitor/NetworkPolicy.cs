using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;

namespace PrinterMonitor;

public sealed class NetworkPolicy
{
    private readonly System.Net.IPNetwork[] networks;
    public NetworkPolicy(MonitorOptions options)
    {
        networks = options.AllowedNetworks.Select(x => System.Net.IPNetwork.Parse(x)).ToArray();
        if (networks.Any(x => x.BaseAddress.AddressFamily != AddressFamily.InterNetwork))
            throw new InvalidOperationException("Bu sürümde AllowedNetworks IPv4 CIDR olmalı.");
    }
    public bool Allows(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork
        && !ip.Equals(IPAddress.Any) && ip.GetAddressBytes()[0] < 224 && networks.Any(x => x.Contains(ip));

    public async Task<IPEndPoint> ResolveAsync(string host, int port, CancellationToken token)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var parsed) ? [parsed] : await Dns.GetHostAddressesAsync(host, token);
        var address = addresses.FirstOrDefault(Allows);
        if (address is null) throw new ValidationException("Hedef izin verilen IPv4 ağlarında değil. Monitor:AllowedNetworks ayarını kontrol edin.");
        return new(address, port);
    }
}
