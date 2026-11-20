using System.Buffers.Binary;

namespace EnbNext.Net7.Protocol;

public sealed class Net7AuthenticationException : InvalidOperationException
{
    public Net7AuthenticationException(Net7AuthLoginResponse response)
        : base(BuildMessage(response))
    {
        Response = response;
    }

    public Net7AuthLoginResponse Response { get; }

    private static string BuildMessage(Net7AuthLoginResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.ReasonText))
            return $"Net7 account login was rejected: {response.ReasonText}.";
        if (!string.IsNullOrWhiteSpace(response.ReasonCode))
            return $"Net7 account login was rejected: reason code {response.ReasonCode}.";
        return "Net7 account login was rejected by the AuthLogin result.";
    }
}

public sealed class Net7IncompatibleVersionException : InvalidOperationException
{
    private Net7IncompatibleVersionException(int? versionHundredths)
        : base(versionHundredths is int value
            ? $"Net7 rejected the client/proxy compatibility version during bootstrap (UDP 0x1009); server reports {value * 0.01:0.00}."
            : "Net7 rejected the client/proxy compatibility version during bootstrap (UDP 0x1009).")
    {
        VersionHundredths = versionHundredths;
        LatestVersion = versionHundredths * 0.01d;
    }

    public int? VersionHundredths { get; }
    public double? LatestVersion { get; }

    public static Net7IncompatibleVersionException FromPayload(ReadOnlySpan<byte> payload)
    {
        // Net7Proxy 0040BE70 formats the version from its receive buffer without
        // a length check; the frame is terminal either way. Safety rule: a short
        // frame has no defined version, so none is reported.
        return new Net7IncompatibleVersionException(
            payload.Length < 4 ? null : BinaryPrimitives.ReadInt32LittleEndian(payload[..4]));
    }
}

public sealed class Net7ServerShutdownException : InvalidOperationException
{
    public Net7ServerShutdownException()
        : base("Net7 requested client shutdown (UDP 0x100A).") { }
}

public sealed class Net7ServerStartingException : InvalidOperationException
{
    public Net7ServerStartingException()
        : base("Net7 reported that the server is still in its startup cycle (UDP 0x100B).") { }
}

public sealed class Net7AvatarSelectionRejectedException : InvalidOperationException
{
    public Net7AvatarSelectionRejectedException(string message) : base(message) { }
}
