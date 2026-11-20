using System.Reflection.PortableExecutable;
using System.Text;

namespace EnbNext.Assets;

/// <summary>Reads the installed launcher's patched BrowserLink action-3 URL.</summary>
public static class EnbRetailNewsAddress
{
    // Current client BrowserLinkManagerCtor 007C97B0 references VA 00B9FF34.
    public static Uri Load(string clientExecutable)
        => LoadLink(clientExecutable, 0x0079FF34);

    internal static Uri LoadLink(string clientExecutable, int relativeAddress)
    {
        using var stream = File.OpenRead(EnbNext.Installation.EnbPathCase.Resolve(clientExecutable));
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.PEHeader?.ImageBase != 0x00400000)
            throw new InvalidDataException("Unsupported retail browser-link image layout.");
        var bytes = pe.GetSectionData(relativeAddress).GetContent(0, 64).AsSpan();
        int end = bytes.IndexOf((byte)0);
        if (end <= 0 || !Uri.TryCreate(Encoding.ASCII.GetString(bytes[..end]), UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0)
            throw new InvalidDataException("The installed retail browser link is invalid.");
        return address;
    }
}
