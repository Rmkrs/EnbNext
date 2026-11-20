using System.Reflection.PortableExecutable;
using System.Text;

namespace EnbNext.Assets;

/// <summary>Current74B03E/74B0A2 local command feedback from the installed client.</summary>
public static class EnbRetailSelfNameCommand
{
    public static bool? Parse(string text)
        => text.Equals("/self tag on",StringComparison.OrdinalIgnoreCase)?true:
            text.Equals("/self tag off",StringComparison.OrdinalIgnoreCase)?false:null;
    public static string Feedback(string executable,bool enabled)
    {
        using var stream=File.OpenRead(EnbNext.Installation.EnbPathCase.Resolve(executable));
        using var pe=new PEReader(stream);
        if(pe.PEHeaders.PEHeader?.ImageBase!=0x400000)throw new InvalidDataException("Unsupported retail command image layout.");
        var bytes=pe.GetSectionData(enabled?0x79a054:0x79a00c).GetContent(0,64).AsSpan();
        int end=bytes.IndexOf((byte)0);
        if(end<0)throw new InvalidDataException("Retail self-name feedback is unterminated.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }
}
