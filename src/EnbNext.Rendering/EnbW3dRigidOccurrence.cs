using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>An owner-qualified ordinary rigid occurrence. Authored descriptors
/// are prepared once; each view supplies new value inputs. Animation/hierarchy,
/// skin, particles and special materials keep their concrete update paths.</summary>
public sealed class EnbW3dRigidOccurrence
{
    internal sealed record Eligibility(bool Value);
    private readonly EnbW3dDrawInvocation _definition;
    private readonly int[] _frameSources;
    public EnbW3dRenderResource Resource { get; }
    public int DescriptorCount => _definition.Packets.Count;
    private EnbW3dRigidOccurrence(EnbW3dRenderResource resource, EnbW3dDrawInvocation definition)
    {
        Resource=resource;_definition=definition;
        _frameSources=new int[definition.Packets.Count];
        var sources=new Dictionary<EnbW3dRenderMesh,int>(ReferenceEqualityComparer.Instance);
        for(int i=0;i<_frameSources.Length;i++)
        {
            var mesh=definition.Packets[i].Mesh;
            if(!sources.TryGetValue(mesh,out int first))sources.Add(mesh,first=i);
            _frameSources[i]=first;
        }
    }

    public static bool CanPrepare(EnbW3dRenderResource resource)
        => OwnerCache.Get(ref resource.DerivedRigidEligibility, resource, static value => new Eligibility(value.HierarchyModels.Count == 0 &&
            value.HierarchicalLods.Count == 0 && value.ExternalRenderObjects.Count == 0 &&
            value.Meshes.Count != 0 && value.Meshes.All(mesh => mesh.CurrentRetailUsesMeshRenderer && mesh.BoneIndices.Count == 0))).Value;

    public static bool TryCreate(IEnbRenderObject owner, EnbW3dRenderResource resource,
        string path, out EnbW3dRigidOccurrence occurrence, out string? diagnostic)
    {
        occurrence = null!; diagnostic = null;
        if (!CanPrepare(resource)) { diagnostic = "Resource requires its animated/generated hierarchy path."; return false; }
        if (!EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner, resource, Matrix4x4.Identity, 0,
            EnbRenderPassKind.OrdinaryWorld, null, EnbW3dLightEnvironment.BlackNoLights,
            out var definition, out diagnostic)) return false;
        for(int i=0;i<definition.Packets.Count;i++)definition.Packets[i]=definition.Packets[i].ForkTask().SetRenderInstancePath(path);
        definition.RetainAfterDrain();
        foreach(var packet in definition.Packets)packet.RetainDefinition();
        occurrence = new(resource, definition);
        return true;
    }

    public bool TrySnapshot(Matrix4x4 world, float modelScale, long time, EnbW3dLightEnvironment lights,
        out EnbW3dDrawInvocation draw, out string? diagnostic)
    {
        draw = null!; diagnostic = null;
        ArgumentNullException.ThrowIfNull(lights);
        if (time < 0) { diagnostic = "W3D frame time cannot be negative."; return false; }
        if (!EnbW3dDrawPacketCompiler.IsFinite(world) || !float.IsFinite(modelScale))
        { diagnostic = "Rigid occurrence has a non-finite transform/scale."; return false; }
        var invocation=EnbW3dDrawInvocation.Create(_definition.ResourceKey,_definition.RenderIdentity,_definition.VisualGeneration,
            time,_definition.HlodSelection,_definition.AnimationSample);
        var snapshots=invocation.Packets;
        for (int i = 0; i < _definition.Packets.Count; i++)
        {
            var prepared = _definition.Packets[i];
            if(_frameSources[i]!=i)
            {
                snapshots.Add(prepared.SnapshotFrameFrom(snapshots[_frameSources[i]]));
                continue;
            }
            if (!EnbW3dDrawPacketCompiler.TryCreateBounds(prepared.Mesh.BoundingSphereCenter,
                prepared.Mesh.BoundingRadius, world, out var bounds))
            { diagnostic = $"Mesh '{prepared.Mesh.Name}' produced invalid world culling bounds."; return false; }
            snapshots.Add(prepared.SnapshotFrame(world,bounds,prepared.DeformedGeometry,lights,time,modelScale));
        }
        draw=invocation;
        return true;
    }
}
