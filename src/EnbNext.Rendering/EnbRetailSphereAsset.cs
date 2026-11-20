using EnbNext.Assets;

namespace EnbNext.Rendering;

// Validated sphere input for standalone and complete aggregate loading.
public sealed record EnbRetailSphereAsset(EnbCurrentRetailSphereHeader Header,
    EnbRetailSphereProperties Properties,bool HasPropertyKeys,bool HasScaleKeys)
{
    public EnbRetailSphereAsset Scale(float factor)
        => factor==1?this:this with {Properties=Properties.Scale(factor)};

    public EnbRetailSphereAsset ShiftColors(System.Numerics.Vector3 shift)
    {
        if(shift==System.Numerics.Vector3.Zero)return this;
        var properties=Properties.ShiftColors(shift);
        return this with {Header=Header with {Color=properties.InitialValues.Color},Properties=properties};
    }

    public EnbCurrentRetailSphereDefinition? Definition {get;init;}

    public static EnbRetailSphereAsset Decode(ReadOnlySpan<byte> bytes)
        => FromDefinition(EnbCurrentRetailSphereDefinition.Decode(bytes));

    public static EnbRetailSphereAsset FromDefinition(EnbCurrentRetailSphereDefinition definition)
        => new(definition.Header,new(definition.Header,definition.Tracks),definition.Tracks.Values.Any(t=>t.Count>0),
            definition.Tracks.TryGetValue(4,out var scale)&&scale.Count>0){Definition=definition};
}
