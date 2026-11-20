using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// Read-only corpus scan used by R6Z4 to locate the current-retail prototype
/// definition that owns an authored HLOD member name. The scan deliberately
/// searches the effective first-registered W3D set rather than inferring a render
/// class from the member's friendly name.
/// </summary>
public static class EnbInstalledW3dPrototypeTokenAudit
{
    private const uint ParticleEmitterChunk = 0x0500;
    private const uint ParticleEmitterHeaderChunk = 0x0501;
    private const uint ContainerFlag = 0x8000_0000u;
    private const uint SizeMask = 0x7FFF_FFFFu;
    private const int ChunkHeaderLength = 8;

    /// <summary>
    /// Mirrors the current-retail Create_Render_Object lazy-load naming path for
    /// a simple prototype name: when a prototype is absent from the manager,
    /// retail attempts &lt;name&gt;.w3d and retries the prototype lookup. This helper
    /// admits the direct resource only when its bytes contain the exact proven
    /// ParticleEmitter 0x0500/0x0501 asset-manager name header for that name.
    /// A filename match by itself is never sufficient.
    /// </summary>
    public static bool TryResolveDirectParticleEmitterPrototype(
        EnbMixResourceCatalog resources,
        string prototypeName,
        out EnbMixResourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentException.ThrowIfNullOrWhiteSpace(prototypeName);
        location = default!;

        // R6Z5 is intentionally bounded to the no-dot current-retail path.
        // The selected-live PWEAPEXHAUST member is in this path. Names carrying
        // a dot have separate current-retail filename construction semantics and
        // remain fail-closed until that case is needed.
        if (prototypeName.Contains(".", StringComparison.Ordinal))
            return false;

        string resourceName = prototypeName + ".w3d";
        if (!resources.TryReadResource(resourceName, out byte[] bytes, out EnbMixResourceLocation resolved))
            return false;

        byte[] token = Encoding.ASCII.GetBytes(prototypeName);
        foreach (int tokenOffset in FindAsciiIgnoreCase(bytes, token))
        {
            TopLevelClassification top = ClassifyTopLevel(bytes, tokenOffset);
            ChildClassification child = top.Type == ParticleEmitterChunk && top.IsContainer
                ? ClassifyDirectChild(bytes, top, tokenOffset)
                : default;
            if (!IsParticleEmitterNameHeaderMatch(bytes, token, tokenOffset, top, child))
                continue;

            location = resolved;
            return true;
        }

        return false;
    }

    public static EnbInstalledW3dPrototypeTokenAuditReport Scan(
        EnbMixResourceCatalog resources,
        string asciiToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentException.ThrowIfNullOrWhiteSpace(asciiToken);

        byte[] token = Encoding.ASCII.GetBytes(asciiToken);
        var matches = new List<EnbInstalledW3dPrototypeTokenMatch>();
        int w3dResources = 0;
        int readableW3dResources = 0;
        int resourcesContainingToken = 0;

        foreach (EnbMixResourceLocation location in resources.Resources)
        {
            if (!string.Equals(Path.GetExtension(location.Name), ".w3d", StringComparison.OrdinalIgnoreCase))
                continue;

            w3dResources++;
            if (!resources.TryReadResource(location.Name, out byte[] bytes, out EnbMixResourceLocation resolved))
                continue;

            readableW3dResources++;
            List<int> tokenOffsets = FindAsciiIgnoreCase(bytes, token);
            if (tokenOffsets.Count == 0)
                continue;

            resourcesContainingToken++;
            foreach (int tokenOffset in tokenOffsets)
            {
                TopLevelClassification top = ClassifyTopLevel(bytes, tokenOffset);
                ChildClassification child = top.Type == ParticleEmitterChunk && top.IsContainer
                    ? ClassifyDirectChild(bytes, top, tokenOffset)
                    : default;
                bool particleEmitterNameHeaderMatch = IsParticleEmitterNameHeaderMatch(
                    bytes,
                    token,
                    tokenOffset,
                    top,
                    child);

                matches.Add(new EnbInstalledW3dPrototypeTokenMatch(
                    resolved.Name,
                    resolved.ArchiveName,
                    bytes.Length,
                    tokenOffset,
                    top.IsClassified,
                    top.Type,
                    top.HeaderOffset,
                    top.IsContainer,
                    child.IsClassified,
                    child.Type,
                    child.HeaderOffset,
                    child.PayloadOffset,
                    child.PayloadLength,
                    child.IsContainer,
                    particleEmitterNameHeaderMatch));
            }
        }

        bool directNamedResourceFound = resources.TryLocateResource(
            asciiToken + ".w3d",
            out EnbMixResourceLocation directNamedResource);

        return new EnbInstalledW3dPrototypeTokenAuditReport(
            asciiToken,
            resources.DiscoveredArchiveCount,
            resources.ArchiveCount,
            resources.ResourceCount,
            w3dResources,
            readableW3dResources,
            resourcesContainingToken,
            directNamedResourceFound ? directNamedResource.Name : null,
            directNamedResourceFound ? directNamedResource.ArchiveName : null,
            matches);
    }

    private static TopLevelClassification ClassifyTopLevel(ReadOnlySpan<byte> bytes, int tokenOffset)
    {
        int offset = 0;
        while (offset <= bytes.Length - ChunkHeaderLength)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            uint sizeAndFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            uint payloadLengthU32 = sizeAndFlags & SizeMask;
            if (payloadLengthU32 > int.MaxValue)
                break;

            int payloadLength = (int)payloadLengthU32;
            int payloadOffset = offset + ChunkHeaderLength;
            if (payloadLength > bytes.Length - payloadOffset)
                break;

            int payloadEnd = payloadOffset + payloadLength;
            if (tokenOffset >= payloadOffset && tokenOffset < payloadEnd)
            {
                return new TopLevelClassification(
                    true,
                    type,
                    offset,
                    payloadOffset,
                    payloadLength,
                    (sizeAndFlags & ContainerFlag) != 0);
            }

            offset = payloadEnd;
        }

        return default;
    }

    private static ChildClassification ClassifyDirectChild(
        ReadOnlySpan<byte> bytes,
        TopLevelClassification top,
        int tokenOffset)
    {
        if (top.PayloadLength <= 0)
            return default;

        int offset = top.PayloadOffset;
        int end = top.PayloadOffset + top.PayloadLength;
        while (offset <= end - ChunkHeaderLength)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            uint sizeAndFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            uint payloadLengthU32 = sizeAndFlags & SizeMask;
            if (payloadLengthU32 > int.MaxValue)
                break;

            int payloadLength = (int)payloadLengthU32;
            int payloadOffset = offset + ChunkHeaderLength;
            if (payloadLength > end - payloadOffset)
                break;

            int payloadEnd = payloadOffset + payloadLength;
            if (tokenOffset >= payloadOffset && tokenOffset < payloadEnd)
            {
                return new ChildClassification(
                    true,
                    type,
                    offset,
                    payloadOffset,
                    payloadLength,
                    (sizeAndFlags & ContainerFlag) != 0);
            }

            offset = payloadEnd;
        }

        return default;
    }


    private static bool IsParticleEmitterNameHeaderMatch(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> token,
        int tokenOffset,
        TopLevelClassification top,
        ChildClassification child)
    {
        // Current retail FUN_00915460 accepts child 0x0501, reads exactly 0x14
        // bytes, and strdup()s the NUL-terminated string beginning at payload
        // +0x04. A token elsewhere in an emitter definition is not evidence
        // that the asset-manager prototype name equals that token.
        if (top.Type != ParticleEmitterChunk ||
            child.Type != ParticleEmitterHeaderChunk ||
            child.PayloadLength != 0x14 ||
            tokenOffset != child.PayloadOffset + 4)
        {
            return false;
        }

        int terminatorOffset = tokenOffset + token.Length;
        int childPayloadEnd = child.PayloadOffset + child.PayloadLength;
        return terminatorOffset < childPayloadEnd &&
               terminatorOffset < bytes.Length &&
               bytes[terminatorOffset] == 0;
    }

    private static List<int> FindAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var matches = new List<int>();
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return matches;

        for (int start = 0; start <= haystack.Length - needle.Length; start++)
        {
            bool equal = true;
            for (int index = 0; index < needle.Length; index++)
            {
                if (ToUpperAscii(haystack[start + index]) == ToUpperAscii(needle[index]))
                    continue;
                equal = false;
                break;
            }

            if (equal)
                matches.Add(start);
        }

        return matches;
    }

    private static byte ToUpperAscii(byte value)
        => value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 0x20) : value;

    private readonly record struct TopLevelClassification(
        bool IsClassified,
        uint Type,
        int HeaderOffset,
        int PayloadOffset,
        int PayloadLength,
        bool IsContainer);

    private readonly record struct ChildClassification(
        bool IsClassified,
        uint Type,
        int HeaderOffset,
        int PayloadOffset,
        int PayloadLength,
        bool IsContainer);
}

public sealed record EnbInstalledW3dPrototypeTokenMatch(
    string ResourceName,
    string ArchiveName,
    int ResourceLength,
    int TokenOffset,
    bool TopLevelChunkClassified,
    uint TopLevelChunkType,
    int TopLevelChunkHeaderOffset,
    bool TopLevelChunkIsContainer,
    bool DirectChildChunkClassified,
    uint DirectChildChunkType,
    int DirectChildChunkHeaderOffset,
    int DirectChildChunkPayloadOffset,
    int DirectChildChunkPayloadLength,
    bool DirectChildChunkIsContainer,
    bool IsParticleEmitterNameHeaderMatch);

public sealed record EnbInstalledW3dPrototypeTokenAuditReport(
    string Token,
    int DiscoveredArchiveCount,
    int ArchiveCount,
    int ActiveResourceCount,
    int W3dResourceCount,
    int ReadableW3dResourceCount,
    int ResourcesContainingToken,
    string? DirectNamedResourceName,
    string? DirectNamedResourceArchiveName,
    IReadOnlyList<EnbInstalledW3dPrototypeTokenMatch> Matches)
{
    public int ParticleEmitterNameHeaderMatchCount
        => Matches.Count(static match => match.IsParticleEmitterNameHeaderMatch);
}
