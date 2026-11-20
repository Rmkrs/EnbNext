using System.Numerics;

namespace EnbNext.Rendering;

// One effect owner. Budget preparation snapshots existing values; only actual
// positive-detail draws advance clocks and replace them. No sibling ownership.
public sealed class EnbRetailSphereAssemblyRuntime(EnbRetailSphereAssembly assembly,float factoryScale=1)
{
    private readonly EnbRetailSphereAssembly poseAssembly=assembly with {Resource=assembly.Resource is {} resource
        ?resource.Copy().SetHierarchies(resource.Hierarchies.Select(tree=>tree.ScaleStoredTranslations(factoryScale)).ToArray()):null};
    private readonly EnbRetailSpherePlayback[] clocks=assembly.Spheres.Select(_=>new EnbRetailSpherePlayback()).ToArray();
    private readonly EnbRetailSphereValues[] values=assembly.Spheres.Select(s=>s.Asset.Properties.Scale(factoryScale).InitialValues).ToArray();
    private readonly EnbRetailSphereAsset[] drawAssets=assembly.Spheres.Select(s=>s.Asset.Scale(factoryScale)).ToArray();
    private readonly float?[] sampledProgress=new float?[assembly.Spheres.Count];
    private float propertyScale=1;
    private readonly Dictionary<int,int> indices=assembly.Spheres.Select((s,i)=>(s.Ordinal,Index:i)).ToDictionary(p=>p.Ordinal,p=>p.Index);
    private EnbRetailSpherePreparedFrame? beforeAllocation,prepared;

    public EnbRetailSpherePreparedFrame BeforeAllocation(ulong serial,uint age,Matrix4x4 ownerRigidWorld,float scale,bool visible,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        if(beforeAllocation is not null)
        {
            if(serial==beforeAllocation.Serial)return beforeAllocation;
            if(serial<beforeAllocation.Serial)throw new ArgumentOutOfRangeException(nameof(serial));
        }
        var pose=poseAssembly.Pose(ownerRigidWorld,scale,age,observed);
        if(scale!=propertyScale)
        {
            // 0090D340 stores scaled current values and keys before 0090D6E0
            // samples them. The API's absolute owner scale is not a draw multiplier.
            for(int i=0;i<drawAssets.Length;i++)
            {
                //48B220's factory scaling precedes6EF160's source-radius
                //scaling. Preserve the float store between those operations.
                var asset=drawAssets[i]=assembly.Spheres[i].Asset.Scale(factoryScale).Scale(scale);
                values[i]=sampledProgress[i] is float sampled?asset.Properties.Evaluate(sampled):asset.Properties.InitialValues;
            }
            propertyScale=scale;
        }
        var items=new List<EnbRetailSpherePreparedAttachment>();
        for(int i=0;i<clocks.Length;i++)
        {
            var binding=assembly.Spheres[i];
            bool admitted=visible&&pose.Visibility[binding.PivotIndex];clocks[i].ReconcileVisibility(admitted);
            if(!admitted)continue;
            var world=pose.WorldTransforms[binding.PivotIndex];
            // Pose scales stored translations only; the basis stays rigid.
            items.Add(Snapshot(binding,world,1,clocks[i].Progress,values[i]));
        }
        return beforeAllocation=new(serial,age,items.AsReadOnly());
    }

    public EnbRetailSpherePreparedFrame Prepare(ulong serial,uint age,uint delta,Matrix4x4 ownerRigidWorld,float scale,bool visible,IReadOnlySet<int> drawn,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        if(beforeAllocation is not null&&serial<beforeAllocation.Serial)throw new ArgumentOutOfRangeException(nameof(serial));
        if(prepared is not null)
        {
            if(serial==prepared.Serial)return prepared;
            if(serial<prepared.Serial)throw new ArgumentOutOfRangeException(nameof(serial));
        }
        var before=BeforeAllocation(serial,age,ownerRigidWorld,scale,visible,observed);
        var items=new List<EnbRetailSpherePreparedAttachment>();
        foreach(var item in before.Spheres)
        {
            var binding=item.Binding;int i=indices[binding.Ordinal];
            if(drawn.Contains(binding.Ordinal))
            {
                clocks[i].Advance(binding.Asset.Header.Duration,binding.Asset.Header.Flags,binding.Asset.HasPropertyKeys,delta);
                if(binding.Asset.HasPropertyKeys)
                {
                    sampledProgress[i]=clocks[i].Progress;
                    values[i]=drawAssets[i].Properties.Evaluate(clocks[i].Progress);
                }
            }
            items.Add(Snapshot(binding,item.World,item.Scale,clocks[i].Progress,values[i]));
        }
        return prepared=new(serial,before.Age,items.AsReadOnly());
    }

    private static EnbRetailSpherePreparedAttachment Snapshot(EnbRetailSphereAttachment binding,Matrix4x4 world,float scale,float progress,EnbRetailSphereValues value)
    {
        var bounds=EnbRetailSphereBounds.World(binding.Asset.Header.Center,binding.Asset.Header.AuthoredExtent,value.Scale*scale,world);
        if(!float.IsFinite(bounds.Radius)||!float.IsFinite(bounds.Center.LengthSquared()))throw new InvalidDataException("Nonfinite sphere frame bounds");
        return new(binding,world,scale,progress,value,bounds);
    }
}

public sealed record EnbRetailSpherePreparedFrame(ulong Serial,uint Age,IReadOnlyList<EnbRetailSpherePreparedAttachment> Spheres);
public sealed record EnbRetailSpherePreparedAttachment(EnbRetailSphereAttachment Binding,Matrix4x4 World,float Scale,float Progress,
    EnbRetailSphereValues Values,EnbW3dWorldBounds Bounds)
{
    public Matrix4x4 DrawWorld(EnbResolvedRenderCamera camera)=>EnbRetailSphereTransform.Build(World,
        Binding.Asset.Header.AuthoredExtent,Values.Scale*Scale,(Binding.Asset.Header.Flags&2)!=0,camera.Basis);
    public EnbRenderDetailOwner Candidate(string name,EnbResolvedRenderCamera camera,int level,float lodBias=1)
    {
        if(!EnbCurrentRetailProjectedDetail.TryCompute(camera,Bounds,out float projected))throw new InvalidDataException("Sphere projected detail unavailable");
        return EnbSphereRingDetailBudgetSemantics.CreateSphereCandidate(name,level,projected,lodBias);
    }
}
