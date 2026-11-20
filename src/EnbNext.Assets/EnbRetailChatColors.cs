using System.Collections.Immutable;

namespace EnbNext.Assets;

/// <summary>7F1AD0 option defaults, loaded in the 765010 palette order.</summary>
public static class EnbRetailChatColors
{
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[] {"Context", "OOC", "Broadcast", "Tell", "Group", "NPC", "Tip", "Local", "Guild", "General_Channel", "OOC_Channel", "Newbie_Channel", "Market_Channel", "Private_Channel", "Race_Channel", "Class_Channel", "Echo", "Error_Context", "Error_OOC", "Error_System", "Combat", "Warning", "Attention", "Debug"});
    public static ImmutableArray<EnbRgb> Load(IReadOnlyDictionary<string,string> options)
        => Enumerable.Range(0,24).Select(type=>
        {
            if(!options.TryGetValue("Color_"+Names[type],out var raw))return Default(type);
            var parts=raw.Split(',');
            if(parts.Length==3 && float.TryParse(parts[0],System.Globalization.CultureInfo.InvariantCulture,out var r) &&
                float.TryParse(parts[1],System.Globalization.CultureInfo.InvariantCulture,out var g) &&
                float.TryParse(parts[2],System.Globalization.CultureInfo.InvariantCulture,out var b))return new EnbRgb(r,g,b);
            return new EnbRgb(.9f,.9f,.9f);
        }).ToImmutableArray();
    public static ImmutableArray<EnbRgb> LoadFile(string path)
    {
        var options=new Dictionary<string,string>();
        if(EnbNext.Installation.EnbPathCase.FileExists(path))foreach(var section in EnbRetailIniDocument.Load(path).EnumerateSections())
            if(section.Value.TryGetValue("OptionName",out var name) && section.Value.TryGetValue("OptionValue",out var value))options[name]=value;
        return Load(options);
    }
    public static ImmutableArray<EnbRgb> Defaults { get; } =
    [
        new(.31f,1,0), new(.9f,.9f,.9f), new(1,.6f,0), new(.99f,.33f,.99f),
        new(.8f,.2f,.8f), new(.31f,1,0), new(.31f,1,0), new(1,.6f,0),
        new(.7f,.7f,1), new(1,.1f,.3f), new(.33f,.99f,.99f), new(.5f,.5f,1),
        new(.5f,1,.5f), new(1,1,1), new(.8f,.8f,.8f), new(.9f,.9f,.5f),
        new(.9f,.9f,.9f), new(.99f,.33f,.33f), new(.9f,.9f,.9f), new(.9f,.9f,.9f),
        new(.9f,.9f,.9f), new(.99f,.33f,.33f), new(.9f,.9f,.9f), new(.33f,.99f,.99f)
    ];
    public static EnbRgb Default(int type) => type >= 0 && type < Defaults.Length ? Defaults[type] : new(1,1,1);
}
