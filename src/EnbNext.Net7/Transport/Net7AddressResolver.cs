using System.Net;
using System.Net.Sockets;

namespace EnbNext.Net7.Transport;

internal static class Net7AddressResolver
{
    public static async Task<IPAddress> ResolveIpv4Async(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException($"EnbNext currently requires an IPv4 Net7 endpoint; got {parsed}.");
            return parsed;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"Host '{host}' did not resolve to IPv4.");
    }
}
