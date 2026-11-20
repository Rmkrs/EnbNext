using System.Buffers.Binary;
using System.Globalization;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7;

public static class Net7StationBrowser
{
    public static Uri AppendQuery(Uri address, string query)
    {
        if (string.IsNullOrEmpty(query) || address.Query.Contains(query, StringComparison.Ordinal)) return address;
        return new UriBuilder(address) { Query = address.Query.Length == 0 ? query : address.Query[1..] + "&" + query }.Uri;
    }

    /// <summary>
    /// Retail 007665A0 formats ClientSessionTask +20/+24 as signed decimal.
    /// Copies this from SectorJoin's half-swapped wire +0C..+13.
    /// The current direct-account path has no GlobalLogin LKey; retail's login
    /// constructors initialize it empty. It is never an alias for EaTicket.
    /// </summary>
    public static string BuildQuery(Net7SectorJoinClientData join)
    {
        byte[] wire = join.ToWireBytes();
        ulong low = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(0x10, 4));
        ulong high = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(0x0C, 4));
        long browserId = unchecked((long)((high << 32) | low));
        return "characterid=" + browserId.ToString(CultureInfo.InvariantCulture) + "&lkey=";
    }
}
