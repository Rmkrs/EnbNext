using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Protocol;

public enum Net7GlobalErrorSource
{
    OuterUdp200E,
    Direct0075,
    AccountTcp0075,
}

/// <summary>
/// Transport/form provenance retained with a normalized GlobalError. The same
/// semantic error family can arrive through materially distinct current paths;
/// the account-TCP compatibility form is not generic direct 0x0075.
/// </summary>
public enum Net7GlobalErrorIngress
{
    Unspecified,
    BootstrapMasterControlUdp,
    MasterControlUdpOuter,
    RoutedUdpOuter,
    DecodedInner,
    ReassembledDecodedInner,
    ServerTcpRole9,
    ServerTcpRole10,
}

/// <summary>
/// Integrated EnbNext transaction context at the point the asynchronous error
/// was observed. This is intentionally separate from wire/form provenance.
/// </summary>
public enum Net7GlobalErrorSessionContext
{
    Unspecified,
    Bootstrap,
    AccountLogin,
    AvatarManagement,
    CharacterSelection,
    AvatarSelection,
    SectorEntry,
    Gameplay,
}

/// <summary>
/// Normalized Global error while preserving distinct wire forms. Outer UDP
/// 0x200E is a little-endian selector extension; generic direct 0x0075 is a
/// big-endian pair plus a NUL-terminated message; the account-TCP compatibility
/// form has a little-endian text length and no terminator.
/// </summary>
public sealed record Net7GlobalError(
    Net7GlobalErrorSource Source,
    uint? Selector,
    uint? OpaqueField0,
    uint? Code,
    string Message,
    bool MalformedTerminator = false)
{
    private static readonly string[] OuterMessages =
    [
        "Error: You have been temporarily banned.",
        "Sorry, that name has already been taken. Please try again.",
        "Sorry, your name can only contain the letters a-z. No spaces or other special characters are allowed.  Please try again.",
        "Sorry, this name is too short. It must contain at least 3 characters. Please try again.",
        "Sorry, this name needs enough vowels (a,e,i,o,u & y) to be pronouncable. Please try again.",
        "Sorry, there are too many repeating characters in this name. Please try again.",
        "Sorry, this name contains a reserved or illegal word. Please try again.",
        "Error: Ticket Validation Failed.",
        "Error: Authentication Server (AUTHD) is unavailable.  Try again in a few minutes.",
        "Error: You have not compleated registration.",
        "Sorry, that ship name is not allowed. Please try again",
        "Sorry, Net7 experienced an internal error. Please submit a bug report",
    ];

    public Net7GlobalErrorIngress Ingress { get; init; } = Net7GlobalErrorIngress.Unspecified;
    public Net7GlobalErrorSessionContext SessionContext { get; init; } = Net7GlobalErrorSessionContext.Unspecified;

    // Retail LoginTask 0076B000 keeps codes 3, 5, 8..13 and 17 nonterminal for
    // every login-task GlobalError (create and delete alike); any other code
    // goes to state 0x0F (error dialog) and then 0x11, which exits the login
    // task (LoginTaskRun 00767F50). The current server message family maps
    // selectors to codes 7..18, so 3 and 5 do not occur there.
    public bool IsLoginTaskRecoverable => Code is 3 or 5 or (>= 8 and <= 13) or 17;

    public static Net7GlobalError DecodeOuterUdp200E(
        ReadOnlySpan<byte> payload,
        Net7GlobalErrorIngress ingress = Net7GlobalErrorIngress.Unspecified,
        Net7GlobalErrorSessionContext sessionContext = Net7GlobalErrorSessionContext.Unspecified)
    {
        if (payload.Length < 4)
            throw new InvalidDataException($"Outer UDP 0x200E payload is {payload.Length} bytes; need at least 4 for the selector.");

        uint selector = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        if (selector < OuterMessages.Length)
        {
            return new Net7GlobalError(
                Net7GlobalErrorSource.OuterUdp200E,
                Selector: selector,
                OpaqueField0: null,
                Code: selector + 7,
                Message: OuterMessages[selector])
            {
                Ingress = ingress,
                SessionContext = sessionContext,
            };
        }

        return new Net7GlobalError(
            Net7GlobalErrorSource.OuterUdp200E,
            Selector: selector,
            OpaqueField0: null,
            Code: null,
            Message: $"Unknown Net7 outer GlobalError selector {selector}.")
        {
            Ingress = ingress,
            SessionContext = sessionContext,
        };
    }

    public static Net7GlobalError DecodeDirect0075(
        ReadOnlySpan<byte> payload,
        Net7GlobalErrorIngress ingress = Net7GlobalErrorIngress.Unspecified,
        Net7GlobalErrorSessionContext sessionContext = Net7GlobalErrorSessionContext.Unspecified)
    {
        if (payload.Length < 8)
            throw new InvalidDataException($"Direct 0x0075 payload is {payload.Length} bytes; need at least 8 plus message bytes.");

        uint opaqueField0 = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
        uint code = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
        ReadOnlySpan<byte> textBytes = payload[8..];
        int nul = textBytes.IndexOf((byte)0);
        bool malformed = nul < 0;
        // 00864C90 uses the bounded CString reader (004AAE90), which
        // retains available text and adds a terminator when the wire omits it.
        if (malformed)
            nul = textBytes.Length;

        string message = Encoding.Latin1.GetString(textBytes[..nul]);
        return new Net7GlobalError(
            Net7GlobalErrorSource.Direct0075,
            Selector: null,
            OpaqueField0: opaqueField0,
            Code: code,
            Message: message,
            MalformedTerminator: malformed)
        {
            Ingress = ingress,
            SessionContext = sessionContext,
        };
    }

    /// <summary>
    /// Recognizes only the known fixed-message compatibility form. The current
    /// server has delivered this form on account TCP role 10 during avatar
    /// creation, although Net7Proxy's role-10 dispatcher discards that opcode.
    /// Its bytes match Net7Proxy's statically proven 0x0075 synthesis: LE text
    /// length, BE code, then exactly that many text bytes without a terminator.
    /// Unknown text or framing must not acquire transaction authority.
    /// </summary>
    public static bool TryDecodeAccountTcp0075(ReadOnlySpan<byte> payload, out Net7GlobalError error)
    {
        error = null!;
        if (payload.Length < 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]) != (uint)(payload.Length - 8))
            return false;

        uint code = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
        if (code < 7 || code >= 7u + (uint)OuterMessages.Length)
            return false;

        string message = Encoding.Latin1.GetString(payload[8..]);
        if (!string.Equals(message, OuterMessages[(int)(code - 7)], StringComparison.Ordinal))
            return false;

        error = new Net7GlobalError(Net7GlobalErrorSource.AccountTcp0075,
            Selector: null, OpaqueField0: null, Code: code, Message: message)
        {
            Ingress = Net7GlobalErrorIngress.ServerTcpRole10,
        };
        return true;
    }

    public static byte[] EncodeDirect0075(uint opaqueField0, uint code, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.IndexOf('\0') >= 0)
            throw new ArgumentException("Direct 0x0075 message cannot contain an embedded NUL.", nameof(message));

        foreach (char c in message)
        {
            if (c > 0x00FF)
                throw new ArgumentException("Direct 0x0075 message must be byte-preserving Latin-1.", nameof(message));
        }

        byte[] text = Encoding.Latin1.GetBytes(message);
        byte[] payload = new byte[9 + text.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), opaqueField0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), code);
        text.CopyTo(payload, 8);
        payload[^1] = 0;
        return payload;
    }
}

public sealed class Net7GlobalErrorException : InvalidOperationException
{
    public Net7GlobalErrorException(Net7GlobalError error)
        : base(BuildMessage(error))
    {
        Error = error;
    }

    public Net7GlobalError Error { get; }

    private static string BuildMessage(Net7GlobalError error)
    {
        string code = error.Code is uint c ? c.ToString() : "unknown";
        string selector = error.Selector is uint s ? $" selector {s}" : string.Empty;
        string ingress = error.Ingress != Net7GlobalErrorIngress.Unspecified ? $" via {error.Ingress}" : string.Empty;
        string context = error.SessionContext != Net7GlobalErrorSessionContext.Unspecified ? $" in {error.SessionContext}" : string.Empty;
        return $"Net7 GlobalError ({error.Source}{selector}{ingress}{context}) code {code}: {error.Message}";
    }
}
