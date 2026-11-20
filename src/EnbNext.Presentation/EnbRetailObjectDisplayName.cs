namespace EnbNext.Presentation;

// Null means missing/unmaterialized; an empty string is a materialized value.
public sealed record EnbSceneDisplayNameProperties(string? Name,string? Owner,string? Title,string? Rank);

public static class EnbRetailObjectDisplayName
{
    // Whole 6FF7A0, shared by scanner readouts and radar/map label producers.
    public static string Format(byte objectType,EnbSceneDisplayNameProperties properties)
    {
        string name=BaseName(objectType,properties);
        if(objectType==0)return name;
        string title=CString(properties.Title??string.Empty);
        string prefix=title.Length!=0?title:CString(properties.Rank??string.Empty);
        return prefix.Length==0?name:prefix+" "+name;
    }

    public static string Resolve(IEnbRenderObject obj) => Format(obj.ObjectType,
        obj.DisplayNameProperties??new(obj.Name,obj.PlayerPresentation?.Owner,obj.PlayerPresentation?.Title,obj.PlayerPresentation?.Rank));

    // 6FF4B0 returns the unadorned recipient, without 6FF7A0's title/rank.
    public static string Recipient(IEnbRenderObject obj) => BaseName(obj.ObjectType,
        obj.DisplayNameProperties??new(obj.Name,obj.PlayerPresentation?.Owner,null,null));

    private static string BaseName(byte objectType,EnbSceneDisplayNameProperties properties)
        => CString((objectType==1?properties.Owner??properties.Name:properties.Name??properties.Owner)??"Unknown");

    public static string? TargetMessageRecipient(IEnbRenderObject? obj)
    {
        // 65E840 requires a materialized player Owner; unlike 6FF4B0 it
        // neither falls back to Name nor adds a title/rank.
        if(obj is not {ObjectType:1})return null;
        string? value=obj.DisplayNameProperties is {} properties?properties.Owner:obj.PlayerPresentation?.Owner;
        if(value is null || (value=CString(value)).Length==0)return null;
        int open=value.IndexOf('['),close=value.IndexOf(']');
        if(open>=0 && close>=0)value=value.Remove(open,Math.Min(value.Length-open,close+2));
        return value;
    }

    private static string CString(string value)
    {
        int end=value.IndexOf('\0');
        return end<0?value:value[..end];
    }
}
