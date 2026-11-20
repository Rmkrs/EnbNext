using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record OrbitalFileFactory(OrdinaryBaseFactory Factory,float Scale,Vector3 Hsv);
    private readonly Dictionary<EnbRetailOrbitalDefinition,OrbitalFileFactory> _orbitalFileFactories=new(ReferenceEqualityComparer.Instance);
    private EnbRetailOrbitalSceneDefinition? _orbitalFactoryScene;
    private (long Assets,ulong Transition) _orbitalFactoryContext;
    private bool _orbitalFactoryLoading;
    private IReadOnlyDictionary<EnbRetailOrbitalDefinition, string>? _movieOrbitalFailures;
    private IReadOnlyDictionary<EnbRetailOrbitalDefinition, EnbCurrentRetailRObjEffectAsset.FactoryPreparation>? _movieOrbitalSelections;

    private void PrepareOrbitalFactories(EnbRetailOrbitalSceneDefinition scene,LiveFrame frame,EnbRetailAssetBundle assets)
    {
        var context=(_assetGeneration,frame.Snapshot.LocationTransition?.Version??0);
        _orbitalFactoryLoading=frame.Snapshot.IsTransitioning;
        if(_orbitalFactoryLoading || !ReferenceEquals(scene,_orbitalFactoryScene) || context!=_orbitalFactoryContext)
        {
            _orbitalFileFactories.Clear();
            _orbitalFactoryScene=_orbitalFactoryLoading?null:scene;
            _orbitalFactoryContext=context;
        }
        _movieOrbitalSelections = frame.Movie?.OrbitalFactories;
        _movieOrbitalFailures = frame.Movie?.OrbitalFactoryFailures;
        if(_orbitalFactoryLoading)return;
        //4F34C0 constructs in authored order before bounds/light queries.
        foreach(var orbital in scene.Orbitals)ResolveOrbitalFileFactory(orbital,assets);
    }

    private OrbitalFileFactory? ResolveOrbitalFileFactory(EnbRetailOrbitalDefinition orbital,EnbRetailAssetBundle assets)
    {
        if(_orbitalFactoryLoading)return null;
        if(_orbitalFileFactories.TryGetValue(orbital,out var retained))return retained;
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(orbital.BaseAsset);
        if(definition.Kind!=EnbBaseAssetDefinitionKind.FileName && !EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(definition))return null;
        var obj=new EnbSceneObjectSnapshot(0,(ulong)_assetGeneration,0,false,false,byte.MaxValue,orbital.BaseAsset,
            orbital.Scale,orbital.HsvH,orbital.HsvS,orbital.HsvV,0,false,"orbital factory",null!,null,null,null,
            [],[],[],[],[],[],null,[],null,0,0);
        //4F2960 uses 6F5150 with authored SCALE/H/S/V. Share ordinary factory
        //construction, but retain its result with the orbital, not a world ID.
        EnbCurrentRetailRObjEffectAsset.FactoryPreparation? selected = null;
        _movieOrbitalSelections?.TryGetValue(orbital, out selected);
        var factory=_movieOrbitalFailures is {} failures && failures.TryGetValue(orbital,out var failure)
            ? new OrdinaryBaseFactory(new(orbital.BaseAsset,EnbBaseAssetResolutionStatus.UnsupportedDefinition,definition,Diagnostic:failure),null,failure)
            : CreateOrdinaryBaseFactory(obj,assets,definition,selected);
        try
        {
            if(factory.Root is {} root)
            {
                if(root.UnconfiguredFactoryObject)obj=obj with {Scale=1,HsvH=0,HsvS=0,HsvV=0};
                if(root.ConfiguredFactoryInputs is {} inputs)
                    obj=obj with {Scale=inputs.Scale,HsvH=inputs.Hsv.X,HsvS=inputs.Hsv.Y,HsvV=inputs.Hsv.Z};
                if(root.ConfiguredFactoryInputs is null && !root.ParticleConstructionPrepared && HasConfigurableMeshTree(root))
                {
                    var appearance=FactoryAppearance.From(obj);
                    root=RetainConfiguredDirectMeshRoot(root,ref appearance);
                    obj=obj with {Scale=appearance.Scale,HsvH=appearance.HsvH,HsvS=appearance.HsvS,HsvV=appearance.HsvV};
                }
                factory=factory with {Root=root};
            }
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException)
        {factory=factory with {Root=null,Diagnostic=error.Message};}
        retained=new(factory,obj.Scale,new(obj.HsvH,obj.HsvS,obj.HsvV));
        _orbitalFileFactories.Add(orbital,retained);
        return retained;
    }
}
