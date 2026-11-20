using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(EnbW3dRenderResource Resource,Vector3 Shift),EnbW3dRenderResource> _harvestColors=[];

    private EnbW3dRenderResource HarvestColors(EnbW3dRenderResource resource,IEnbRenderObject obj,EnbRetailAssetBundle assets)
    {
        if(obj.ObjectType!=0x26)return resource;
        if(resource.Meshes.Any(mesh=>_configuredDirectMeshModels.ContainsKey(mesh) ||
            _shipExhaustOwner?._configuredDirectMeshModels.ContainsKey(mesh)==true))return resource;
        var shift=new Vector3(obj.HsvH,obj.HsvS,obj.HsvV);
        // 0048B220 prototype adjustment threshold, shared with effect factories.
        if(MathF.Abs(shift.X)<=1 && MathF.Abs(shift.Y)<=.01f && MathF.Abs(shift.Z)<=.01f)return resource;
        if(_harvestColors.TryGetValue((resource,shift),out var cached))return cached;
        var adjusted=EnbCurrentRetailEffectMeshHsvRuntime.Apply(resource,assets.Textures,shift);
        adjusted=adjusted.Copy().SetExternalRenderObjects(resource.ExternalRenderObjects.Select(external=>
        {
            if(external.ParticleEmitterDefinition is not {} emitter)return external;
            var texture=external.ParticleRetainedTexture is {} retained
                ? EnbCurrentRetailEffectMeshHsvRuntime.ResolveRetainedTexture(assets.Textures,retained,shift) : null;
            return external with {ParticleRetainedTexture=texture,ParticleEmitterDefinition=emitter.Copy().SetTextureFileName(texture?.LogicalName??emitter.TextureFileName).SetColor(EnbCurrentRetailHsvRuntime.ShiftParticleColor(emitter.Color,shift))};
        }).ToArray());
        return _harvestColors[(resource,shift)]=adjusted;
    }
}
