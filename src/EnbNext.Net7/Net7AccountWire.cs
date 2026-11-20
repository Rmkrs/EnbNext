using System.Buffers.Binary;
using System.Text;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7;

public static class Net7AccountWire
{
    // Exact custom encoder set. This deliberately is not generic
    // form/RFC URL encoding.
    private static readonly bool[] AuthLoginEscapedByte = BuildEscapedByteTable();

    public static byte[] BuildAccountLoginPayload(string username, string password)
    {
        byte[] user = EscapeAuthLoginCredentialBytes(username, nameof(username));
        byte[] pass = EscapeAuthLoginCredentialBytes(password, nameof(password));
        ValidateLengthPrefixedField(user, nameof(username));
        ValidateLengthPrefixedField(pass, nameof(password));

        int total = checked(2 + user.Length + 2 + pass.Length);
        if (total > Packet.MaxPayloadSize)
        {
            throw new ArgumentException(
                $"Escaped account-login payload is {total} bytes; Net7 TCP payload maximum is {Packet.MaxPayloadSize}.");
        }

        byte[] payload = new byte[total];
        int offset = WriteLengthPrefixedBytes(payload, 0, user);
        WriteLengthPrefixedBytes(payload, offset, pass);
        return payload;
    }

    /// <summary>Exact username/password byte escaping.</summary>
    public static string EscapeAuthLoginCredential(string value)
        => Encoding.ASCII.GetString(EscapeAuthLoginCredentialBytes(value, nameof(value)));

    /// <summary>
    /// Retail ownership boundary. AuthLogin itself is length-aware
    /// and can preserve embedded NUL bytes in a parsed Ticket field, but retail
    /// then copies the Ticket through C-string semantics into LoginTask EATicket
    /// storage. Integrated EnbNext owns that same C-string projection directly.
    /// </summary>
    public static string OwnEaTicketFromAuthLoginTicket(string authLoginTicket)
    {
        ArgumentNullException.ThrowIfNull(authLoginTicket);

        int nul = authLoginTicket.IndexOf('\0');
        string eaTicket = nul >= 0 ? authLoginTicket[..nul] : authLoginTicket;
        // 0076A7B0 copies the ticket into its 0x101-byte EATicket buffer with
        // CopyCStringBounded (at most 0x100 bytes plus NUL); its release
        // assertion for a longer ticket only logs, so the copied prefix is used.
        if (GetLatin1CStringBytes(eaTicket, nameof(authLoginTicket)).Length > 0x100)
            eaTicket = eaTicket[..0x100];

        return eaTicket;
    }

    /// <summary>
    /// Full owned EATicket bytes for server TCP 0x2002. The value is not reduced
    /// to the username token and no NUL or length prefix is appended.
    /// </summary>
    public static byte[] BuildAvatarListRequest(string eaTicket)
    {
        byte[] ticket = GetLatin1CStringBytes(eaTicket, nameof(eaTicket));
        if (ticket.Length > Packet.MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eaTicket), $"EA ticket exceeds Net7 TCP payload maximum {Packet.MaxPayloadSize}.");
        }

        return ticket;
    }

    /// <summary>
    /// Current Net7Proxy derives the username used by TCP 0x2004 by applying
    /// strtok(ticket, "-") to the EATicket received from AuthLogin. Preserve
    /// that compatibility boundary instead of assuming the originally typed name.
    /// </summary>
    public static string? ExtractAvatarLoginUsernameFromEaTicket(string eaTicket)
    {
        ArgumentNullException.ThrowIfNull(eaTicket);
        if (eaTicket.IndexOf('\0') >= 0)
            throw new InvalidDataException("EA ticket contains an embedded NUL and cannot be represented by the current C-string compatibility path.");

        ReadOnlySpan<char> ticket = eaTicket.AsSpan();
        int start = 0;
        while (start < ticket.Length && ticket[start] == '-')
            start++;

        // Net7Proxy 00401170 passes strtok's NULL to 00414240, which then
        // copies nothing and leaves the previously stored username in place.
        if (start == ticket.Length)
            return null;

        int end = start;
        while (end < ticket.Length && ticket[end] != '-')
            end++;

        return ticket[start..end].ToString();
    }

    public static byte[] BuildAvatarLoginPayload(int characterSlot, string username)
    {
        if (characterSlot is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(characterSlot), "Current GlobalAvatarList has slots 0..4.");

        byte[] user = GetLatin1CStringBytes(username, nameof(username));
        ValidateLengthPrefixedField(user, nameof(username));

        int total = checked(4 + 2 + user.Length);
        if (total > Packet.MaxPayloadSize)
            throw new ArgumentException($"Avatar-login payload is {total} bytes; Net7 TCP payload maximum is {Packet.MaxPayloadSize}.");

        byte[] payload = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), characterSlot);
        WriteLengthPrefixedBytes(payload, 4, user);
        return payload;
    }


    public const int AvatarCreatePayloadSize = 0x21B;

    /// <summary>Exact direct account TCP 0x200B body.</summary>
    public static byte[] BuildAvatarCreatePayload(Protocol.Net7AvatarCreateRequest request, string accountUsername)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Avatar);
        ArgumentNullException.ThrowIfNull(request.ShipAppearance);
        if (request.TargetSlot is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(request.TargetSlot), "Current create target slot must be 0..4.");

        byte[] username = GetLatin1CStringBytes(accountUsername, nameof(accountUsername));
        byte[] avatar = Protocol.Net7AvatarCreateCodec.EncodeAvatarData(request.Avatar);
        byte[] ship = request.ShipAppearance.ToArray();

        byte[] payload = new byte[AvatarCreatePayloadSize];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x000, 4), request.TargetShardId);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x004, 4), checked((uint)request.TargetSlot));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x008, 4), request.Control);

        // Retail strncpy(..., 0x40) + forced byte 64 NUL. The zero-initialized
        // array also reproduces ordinary strncpy padding for shorter usernames.
        int userCopy = Math.Min(username.Length, 0x40);
        username.AsSpan(0, userCopy).CopyTo(payload.AsSpan(0x00C, 0x40));
        payload[0x04C] = 0;

        avatar.CopyTo(payload, 0x04D);
        // 00864610 appends both serializable objects in sequence; AvatarData's
        // Skeleton is a C string (004BF260), normally empty in player defaults.
        // The proxy still forwards a fixed 0x21B envelope (00414560). Keep unused
        // tail bytes zero rather than copying retail's uninitialized allocation.
        ship.CopyTo(payload, 0x04D + avatar.Length);
        return payload;
    }

    /// <summary>Exact direct account TCP 0x200D body.</summary>
    public static byte[] BuildAvatarDeletePayload(int characterSlot, string username)
    {
        if (characterSlot is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(characterSlot), "Current GlobalAvatarList has slots 0..4.");

        byte[] user = GetLatin1CStringBytes(username, nameof(username));
        ValidateLengthPrefixedField(user, nameof(username));
        int total = checked(4 + 2 + user.Length);
        if (total > Packet.MaxPayloadSize)
            throw new ArgumentException($"Avatar-delete payload is {total} bytes; Net7 TCP payload maximum is {Packet.MaxPayloadSize}.");

        byte[] payload = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), checked((uint)characterSlot));
        WriteLengthPrefixedBytes(payload, 4, user);
        return payload;
    }

    public static byte[] BuildMasterLoginPayload(uint selectedSector, Net7FeatureFlags featureFlags)
    {
        const Net7FeatureFlags knownFlags =
            Net7FeatureFlags.UdpPacketOptimisation |
            Net7FeatureFlags.SpMode |
            Net7FeatureFlags.SectorCaching;
        if ((featureFlags & ~knownFlags) != 0)
            throw new ArgumentOutOfRangeException(nameof(featureFlags), featureFlags, "Unknown Net7 Master capability bits are not permitted.");

        int length = featureFlags == Net7FeatureFlags.None ? 4 : 5;
        byte[] payload = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), selectedSector);
        if (length == 5)
            payload[4] = (byte)featureFlags;
        return payload;
    }

    public static byte[] BuildSectorPortConfirmationPayload(uint selectedSector)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, selectedSector);
        return payload;
    }

    private static int WriteLengthPrefixedBytes(byte[] destination, int offset, byte[] bytes)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, 2), checked((ushort)bytes.Length));
        bytes.CopyTo(destination, offset + 2);
        return offset + 2 + bytes.Length;
    }

    private static void ValidateLengthPrefixedField(byte[] bytes, string parameterName)
    {
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "Value is too long for Net7's u16 length prefix.");
    }

    private static byte[] EscapeAuthLoginCredentialBytes(string value, string parameterName)
    {
        byte[] source = GetLatin1CStringBytes(value, parameterName);
        int encodedLength = 0;
        foreach (byte b in source)
            encodedLength = checked(encodedLength + (AuthLoginEscapedByte[b] ? 3 : 1));

        byte[] encoded = new byte[encodedLength];
        int destination = 0;
        foreach (byte b in source)
        {
            if (!AuthLoginEscapedByte[b])
            {
                encoded[destination++] = b;
                continue;
            }

            encoded[destination++] = (byte)'%';
            encoded[destination++] = ToUpperHex((byte)(b >> 4));
            encoded[destination++] = ToUpperHex((byte)(b & 0x0F));
        }

        return encoded;
    }

    private static byte[] GetLatin1CStringBytes(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.IndexOf('\0') >= 0)
            throw new ArgumentException("Embedded NUL is not representable by the current C-string compatibility boundary.", parameterName);

        foreach (char c in value)
        {
            if (c > 0x00FF)
            {
                throw new ArgumentException(
                    $"Character U+{(int)c:X4} is outside the byte-preserving Latin-1 input boundary used by this compatibility codec.",
                    parameterName);
            }
        }

        return Encoding.Latin1.GetBytes(value);
    }

    private static byte ToUpperHex(byte nibble)
        => (byte)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));

    private static bool[] BuildEscapedByteTable()
    {
        var table = new bool[256];
        foreach (byte b in new byte[]
        {
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
            0x2A, 0x2B, 0x2C, 0x2F,
            0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40,
            0x5B, 0x5C, 0x5D, 0x5E, 0x60,
            0x7B, 0x7C, 0x7D, 0x7E,
        })
        {
            table[b] = true;
        }

        return table;
    }
}
