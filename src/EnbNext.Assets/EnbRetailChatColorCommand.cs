using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;

namespace EnbNext.Assets;

/// <summary>747FA0 local palette commands; feedback addresses belong to the current retail image.</summary>
public sealed record EnbRetailChatColorCommand(bool Reset, int Type, EnbRgb? Color,
    bool Store, IReadOnlyList<EnbRetailChatColorFeedback> Feedback)
{
    private static readonly string[] Names = ["context","ooc","broadcast","tell","group","npc","tip","local","guild","general","ooc_channel","new_player","market","private","race","class","echo","error_context","error_ooc","error_system","combat","warning"];
    private static readonly int[] SetText = [0xb99980,0xb9994c,0xb9990c,0xb99314,0xb998d4,0xb998b0,0xb99888,0xb99850,0xb99820,0xb997d8,0xb99790,0xb99744,0xb99700,0xb996b8,0xb99680,0xb99640,0xb99614,0xb995d0,0xb9958c,0xb9954c,0xb99514,0xb994e0];
    private static readonly int[] SeeText = [0xb99280,0xb9924c,0xb99214,0xb98df0,0xb991e0,0xb991b8,0xb99190,0xb9915c,0xb99128,0xb990e8,0xb990ac,0xb99068,0xb99028,0xb98fe8,0xb98fac,0xb98f6c,0xb98f44,0xb98f0c,0xb98ed0,0xb98e9c,0xb98e68,0xb98e3c];

    public static EnbRetailChatColorCommand? Parse(string text)
    {
        if(text.StartsWith("/resetcolor",StringComparison.Ordinal))
            return new(true,-1,null,true,[new(19,0xb999c4,false)]);
        bool set=text.StartsWith("/setcolor",StringComparison.Ordinal);
        if(!set && !text.StartsWith("/seecolor",StringComparison.Ordinal))return null;
        // CopyCStringBounded(256), then strtok with only space and tab delimiters.
        int end=text.IndexOf('\0');
        text=text[..Math.Min(end<0?text.Length:end,255)];
        var words=text.Split([' ','\t'],StringSplitOptions.RemoveEmptyEntries);
        var feedback=new List<EnbRetailChatColorFeedback>();
        void Usage(bool invalid)
        {
            feedback.Add(new(6,set?0xb99468:0xb992b0,false));
            feedback.Add(new(6,0xb9937c,false));
            if(set)feedback.Add(new(6,invalid?0xb99340:0xb992d8,false));
        }
        if(words.Length<(set?5:2)) {Usage(false);return new(false,-1,null,false,feedback);}
        int type=words[1].Equals("reply",StringComparison.OrdinalIgnoreCase)?3:
            Array.FindIndex(Names,n=>n.Equals(words[1],StringComparison.OrdinalIgnoreCase));
        if(type<0)
        {
            feedback.Add(new(6,set?0xb994a0:0xb98e20,true));Usage(true);
            return new(false,-1,null,set,feedback);
        }
        EnbRgb? color=set?new(Component(words[2]),Component(words[3]),Component(words[4])):null;
        return new(false,type,color,set,[new((byte)type,set?SetText[type]:SeeText[type],true)]);
    }

    // A278F6 -> A32F11 -> A3D932: decimal numeric prefix, optional D/E exponent,
    // invalid input zero; double return -> float, then 765E40 clamps each channel.
    public static float Component(string text)
    {
        int start=0;
        while(start<text.Length && text[start] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')start++;
        int at=start;
        if(at<text.Length && text[at] is '+' or '-')at++;
        int digits=0;
        while(at<text.Length && char.IsAsciiDigit(text[at])) {at++;digits++;}
        if(at<text.Length && text[at]=='.')
        {at++;while(at<text.Length && char.IsAsciiDigit(text[at])) {at++;digits++;}}
        if(digits==0)return 0;
        int mantissaEnd=at;
        if(at<text.Length && text[at] is 'e' or 'E' or 'd' or 'D')
        {
            at++;if(at<text.Length && text[at] is '+' or '-')at++;
            int exponentStart=at;
            while(at<text.Length && char.IsAsciiDigit(text[at]))at++;
            if(at==exponentStart)at=mantissaEnd;
        }
        string prefix=text[start..at].Replace('d','e').Replace('D','E');
        double.TryParse(prefix,NumberStyles.Float,CultureInfo.InvariantCulture,out double value);
        float component=(float)value;
        return component>0?Math.Min(component,1):0;
    }
}

public sealed record EnbRetailChatColorFeedback(byte Type,int Address,bool SuppressPrefix)
{
    public string Read(string executable)
    {
        using var stream=File.OpenRead(EnbNext.Installation.EnbPathCase.Resolve(executable));
        using var pe=new PEReader(stream);
        if(pe.PEHeaders.PEHeader?.ImageBase!=0x400000)throw new InvalidDataException("Unsupported retail command image layout.");
        var bytes=pe.GetSectionData(Address-0x400000).GetContent(0,512).AsSpan();
        int end=bytes.IndexOf((byte)0);
        if(end<0)throw new InvalidDataException("Retail chat color feedback is unterminated.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }
}
