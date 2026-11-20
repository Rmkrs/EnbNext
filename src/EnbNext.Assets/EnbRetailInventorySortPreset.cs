namespace EnbNext.Assets;

public sealed record EnbRetailInventorySortPreset(string Name,uint Primary,uint Secondary,uint Tertiary)
{
    // ConstructOptionsManager 7F55EE..7F594F and 7F680F..7F68F7.
    public static IReadOnlyList<EnbRetailInventorySortPreset> Defaults { get; } =
        Array.AsReadOnly(new EnbRetailInventorySortPreset[]{new("category",5,4,1),new("name",1,4,8),new("value",10,1,8)});
}
