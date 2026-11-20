using System.Numerics;

namespace EnbNext.Rendering;

// One owner, one asset plan. A monotonic frame serial is separate from wrapped
// game time; repeated preparation of that serial returns the original snapshot.
public sealed class EnbRetailRingAssemblyRuntime(EnbRetailRingAssembly assembly,float factoryScale=1)
{
    private readonly EnbRetailRingAssembly poseAssembly=assembly with {Resource=assembly.Resource is {} resource
        ?resource.Copy().SetHierarchies(resource.Hierarchies.Select(tree=>tree.ScaleStoredTranslations(factoryScale)).ToArray()):null};
    private readonly EnbRetailRingPlayback[] clocks=assembly.Rings.Select(_=>new EnbRetailRingPlayback()).ToArray();
    private readonly EnbRetailRingValues[] storedValues=assembly.Rings.Select(r=>r.Asset.ScaleRadiusMultipliers(factoryScale).InitialValues).ToArray();
    private readonly Vector2[] storedExtents=assembly.Rings.Select(r=>r.Asset.Header.SecondRadii).ToArray();
    private readonly EnbRetailRingAsset[] drawAssets=assembly.Rings.Select(r=>r.Asset.ScaleRadiusMultipliers(factoryScale)).ToArray();
    private readonly float?[] storedSampleProgress=new float?[assembly.Rings.Count];
    private float propertyScale=1;
    private readonly Dictionary<int,int> indices=assembly.Rings.Select((r,i)=>(r.Ordinal,Index:i)).ToDictionary(p=>p.Ordinal,p=>p.Index);
    private EnbRetailRingPreparedFrame? beforeAllocation;
    private EnbRetailRingPreparedFrame? prepared;

    // Allocation observes existing extents with this frame's hierarchy pose.
    // It must precede Prepare; repeated calls also retain the first placement.
    public EnbRetailRingPreparedFrame BeforeAllocation(ulong frameSerial,uint age,
        Matrix4x4 ownerRigidWorld,float scale,bool ownerVisible,EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        if(beforeAllocation is not null)
        {
            if(frameSerial==beforeAllocation.Serial)return beforeAllocation;
            if(frameSerial<beforeAllocation.Serial)throw new ArgumentOutOfRangeException(nameof(frameSerial));
        }
        if(scale!=propertyScale)
        {
            for(int i=0;i<drawAssets.Length;i++)
            {
                //48B220 and6EF160 invoke separate property-scaling passes.
                var asset=drawAssets[i]=assembly.Rings[i].Asset.ScaleRadiusMultipliers(factoryScale).ScaleRadiusMultipliers(scale);
                storedValues[i]=storedSampleProgress[i] is float sampled?asset.EvaluateValues(sampled):asset.InitialValues;
                if(storedSampleProgress[i].HasValue&&asset.Tracks.TryGetValue(5,out var keys)&&keys.Count>0)
                    storedExtents[i]=storedValues[i].SecondRadii;
            }
            propertyScale=scale;
        }
        var pose=poseAssembly.Pose(ownerRigidWorld,scale,age,observed);
        var rings=new List<EnbRetailRingPreparedAttachment>();
        for(int i=0;i<clocks.Length;i++)
        {
            var binding=assembly.Rings[i];
            bool visible=ownerVisible && pose.Visibility[binding.PivotIndex];
            clocks[i].ReconcileVisibility(visible);
            var world=pose.WorldTransforms[binding.PivotIndex];
            if(visible)rings.Add(new(binding,world,clocks[i].Progress,storedValues[i],
                WorldBounds(binding,world,storedExtents[i])));
        }
        return beforeAllocation=new(frameSerial,age,rings.AsReadOnly());
    }

    public EnbRetailRingPreparedFrame Prepare(ulong frameSerial,uint age,uint deltaMilliseconds,
        Matrix4x4 ownerRigidWorld,float scale,bool ownerVisible,IReadOnlySet<int> drawnOrdinals,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        if(beforeAllocation is not null && frameSerial<beforeAllocation.Serial)throw new ArgumentOutOfRangeException(nameof(frameSerial));
        if(prepared is not null)
        {
            if(frameSerial==prepared.Serial)return prepared;
            if(frameSerial<prepared.Serial)throw new ArgumentOutOfRangeException(nameof(frameSerial));
        }
        var before=BeforeAllocation(frameSerial,age,ownerRigidWorld,scale,ownerVisible,observed);
        var rings=new List<EnbRetailRingPreparedAttachment>();
        foreach(var ring in before.Rings)
        {
            var binding=ring.Binding;
            int i=indices[binding.Ordinal];
            // Native Render skips its update at detail zero. Allocation must
            // already have selected which bindings will actually draw this frame.
            if(drawnOrdinals.Contains(binding.Ordinal))
            {
                clocks[i].Advance(binding.Asset,true,deltaMilliseconds);
                if(binding.Asset.Tracks.Values.Any(track=>track.Count>0))
                {
                    storedSampleProgress[i]=clocks[i].Progress;
                    storedValues[i]=drawAssets[i].EvaluateValues(clocks[i].Progress);
                    if(binding.Asset.Tracks.TryGetValue(5,out var secondTrack)&&secondTrack.Count>0)
                        storedExtents[i]=storedValues[i].SecondRadii;
                }
            }
            rings.Add(new(binding,ring.World,clocks[i].Progress,storedValues[i],WorldBounds(binding,ring.World,storedExtents[i])));
        }
        return prepared=new(frameSerial,before.Age,rings.AsReadOnly());
    }

    private static EnbW3dWorldBounds WorldBounds(EnbRetailRingAttachment binding,Matrix4x4 world,Vector2 extents)
    {
        // 009421B0 transforms the center but copies the local sphere radius.
        // 00912480 does not change the stored extents; only a second-key update does.
        var bounds=new EnbW3dWorldBounds(Vector3.Transform(binding.Asset.Header.Center,world),extents.Length());
        if(!float.IsFinite(bounds.Radius)||!float.IsFinite(bounds.Center.X)||!float.IsFinite(bounds.Center.Y)||!float.IsFinite(bounds.Center.Z))
            throw new InvalidDataException("Nonfinite prepared ring bounds");
        return bounds;
    }
}

public sealed record EnbRetailRingPreparedFrame(ulong Serial,uint Age,IReadOnlyList<EnbRetailRingPreparedAttachment> Rings);

public sealed record EnbRetailRingPreparedAttachment(EnbRetailRingAttachment Binding,Matrix4x4 World,float Progress,
    EnbRetailRingValues Values,EnbW3dWorldBounds Bounds)
{
    public Matrix4x4 DrawWorld(EnbResolvedRenderCamera camera)=>EnbRetailRingTransform.Build(World,Binding.Asset.Header.Flags,camera);

    public EnbRenderDetailOwner Candidate(string name,EnbResolvedRenderCamera camera,int currentLevel,float lodBias=1)
    {
        if(!EnbCurrentRetailProjectedDetail.TryCompute(camera,Bounds,out float projected))
            throw new InvalidDataException("Ring projected detail unavailable");
        return EnbSphereRingDetailBudgetSemantics.CreateRingCandidate(name,currentLevel,projected,lodBias);
    }
}
