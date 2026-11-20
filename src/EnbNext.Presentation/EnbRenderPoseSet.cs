using System.Collections.Immutable;
using System.Runtime.CompilerServices;
namespace EnbNext.Presentation;

/// <summary>One render generation's UI-thread-owned pose. Protocol writers never mutate it.</summary>
public sealed class EnbRenderPoseOwner
{
    public EnbRenderPose Value;
}

/// <summary>Direct references to mutable presentation owners, not a frame publication.
/// Detach only at an explicit diagnostic/history boundary. Physical diagnostic
/// object snapshots remain separate from the visual pose.</summary>
[CollectionBuilder(typeof(EnbRenderPoseSet), nameof(Create))]
public readonly struct EnbRenderPoseSet
{
    private readonly EnbRenderPoseOwner?[]? owners;
    public EnbRenderPoseSet(EnbRenderPoseOwner?[] owners) => this.owners=owners;
    public bool IsDefault => owners is null;
    public int Length => owners?.Length??0;
    public EnbRenderPose this[int slot] => owners![slot]?.Value??default;
    public EnbRenderPoseOwner? Owner(int slot) => owners![slot];
    public static EnbRenderPoseSet Create(ReadOnlySpan<EnbRenderPose> values)
    {
        var result=new EnbRenderPoseOwner?[values.Length];
        for(int i=0;i<values.Length;i++)result[i]=new(){Value=values[i]};
        return new(result);
    }
    public static implicit operator EnbRenderPoseSet(ImmutableArray<EnbRenderPose> values)
        => values.IsDefault?default:Create(values.AsSpan());
    public EnbRenderPoseSet Detach()
    {
        if(IsDefault)return default;
        var result=new EnbRenderPoseOwner?[Length];
        for(int i=0;i<Length;i++)if(owners![i] is {} owner)result[i]=new(){Value=owner.Value};
        return new(result);
    }
    public IEnumerator<EnbRenderPose> GetEnumerator()
    {for(int i=0;i<Length;i++)yield return this[i];}
    public void Write(IEnbRenderObject value)
    {
        if(value.RenderPoseSlot<0||IsDefault)return;
        var owner=owners![value.RenderPoseSlot];if(owner is null)return;
        var pose=EnbRenderPose.FromObject(value);
        owner.Value=owner.Value with {HasSample=pose.HasSample,Sample=pose.Sample,
            HasVisualInputs=true,ShipVisualBankRadians=pose.ShipVisualBankRadians,ShipEmote=pose.ShipEmote};
    }
    public void WriteWarp(int slot,int engine,int cached,ImmutableArray<EnbSceneWarpPresentationChange> callbacks)
    {
        if(slot<0||IsDefault)return;
        if(owners![slot] is {} owner)owner.Value=owner.Value with {HasWarpInputs=true,EngineWarpState=engine,CachedWarpState=cached,WarpChanges=callbacks};
    }
    public void WritePrivateWarp(int slot,ImmutableArray<EnbSceneWarpPresentationChange> callbacks)
    {
        if(slot<0||IsDefault)return;
        if(owners![slot] is {} owner)owner.Value=owner.Value with {HasPrivateWarpInputs=true,PrivateWarpChanges=callbacks};
    }
    public EnbRenderPoseSet WithTransform(int slot,IEnbRenderTransform transform)
    {
        if(slot<0||IsDefault)return this;
        if(owners![slot] is {} owner)
        {var pose=EnbRenderPose.FromTransform(transform);owner.Value=owner.Value with {HasSample=pose.HasSample,Sample=pose.Sample};}
        return this;
    }
}
