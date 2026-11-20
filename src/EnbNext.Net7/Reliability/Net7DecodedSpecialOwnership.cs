using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Reliability;

/// <summary>
/// Ownership boundary for decoded-inner specials that are not themselves
/// reliability mechanics. The reliability decoder must recognize them as valid translated/suppressed cases
/// so it does not misclassify them as bad high opcodes.
/// The world specials are consumed before this fallback; the gameplay domain owns
/// these five gameplay-domain specials directly in the integrated client.
/// The legacy proxy short-0x001B predicate is exposed only for compatibility-history
/// fixtures; never apply that malformed-length quirk
/// in the integrated EnbNext receive path.
/// </summary>
public static class Net7DecodedSpecialOwnership
{
    public static bool IsLegacyProxyMalformedShortAuxData(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        // In decoded-inner form the caller supplies the complete inner length.
        return packet.Header.Opcode == 0x001B && packet.Header.Size < 8;
    }

    public static bool IsGameplayDomainSpecial(ushort opcode)
        => opcode is
            0x2012 or // Prospect extension
            0x2013 or // Tractor A
            0x2014 or // Tractor B
            0x2025 or // local CData request
            0x202A;   // delayed warp termination
}
