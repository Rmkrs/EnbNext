using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Installed-resource ownership for a single admitted movie, borrowing the asset-manager namespace.</summary>
public sealed class EnbRetailMovieAssets : IEnbRetailMovieResources
{
    private readonly EnbRetailAssetBundle _assets;
    private readonly EnbW3dRenderResourceCache _cache;
    private readonly EnbW3dTimeMappedAcquisition _animations;
    private readonly List<EnbRetailMovieEffect> _effects = [];
    private readonly List<EnbRetailMovieSound> _sounds = [];
    private readonly List<EnbCurrentRetailTextureRegistry.Lease?> _starTextures = [];
    private ulong _identity;
    private int _effectId;
    private long _soundId;
    private readonly Dictionary<(ulong, int), uint> _boneIds = [];
    private uint _nextBoneId = 0x80000000;
    private bool _disposed, _orbitalCreated;
    private readonly List<EnbRetailMovieModel> _modelOwners=[];
    public Func<EnbRetailMovieModel,IDisposable>? ModelFactory {get;set;}
    public Action<EnbEffectDefinition,Vector3>? PassEffectPreloader {get;set;}
    public Action<EnbEffectDefinition,float,Vector3>? ParticleJetEffectPreloader {get;set;}
    public Action<EnbEffectDefinition,EnbCurrentRetailRObjEffectAsset,float,Vector3>? ObjectEffectPreloader {get;set;}
    public Func<EnbEffectDefinition,float,Vector3,EnbCurrentRetailRObjEffectAsset.FactoryPreparation?>? RetainedObjectEffectFactory {get;set;}
    public string? Diagnostic { get; private set; }
    public EnbBaseAssetResolution? Skybox { get; private set; }
    public EnbRetailStarLayerSelection? Stars { get; private set; }
    public int StarConstructorArgument { get; private set; }
    public IReadOnlyList<EnbCurrentRetailTextureRegistry.Lease?> StarTextures => _starTextures;
    public EnbRetailOrbitalSceneDefinition? Orbitals { get; private set; }
    public float OrbitalHours { get; private set; }
    public IReadOnlyDictionary<EnbRetailOrbitalDefinition, EnbCurrentRetailRObjEffectAsset.FactoryPreparation> OrbitalFactories => _orbitalFactories;
    public IReadOnlyDictionary<EnbRetailOrbitalDefinition, string> OrbitalFactoryFailures => _orbitalFactoryFailures;
    private readonly Dictionary<EnbRetailOrbitalDefinition, string> _orbitalFactoryFailures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EnbRetailOrbitalDefinition, EnbCurrentRetailRObjEffectAsset.FactoryPreparation> _orbitalFactories = new(ReferenceEqualityComparer.Instance);
    public IReadOnlyList<EnbRetailMovieEffect> Effects => _effects;
    public IReadOnlyList<EnbRetailMovieSound> Sounds => _sounds;
    public Action<int, uint>? EffectDeadline { get; set; }
    public EnbRetailMovieShake CameraShake { get; } = new();
    public Func<uint> WallClock { get; init; } = () => unchecked((uint)System.Environment.TickCount64);

    public EnbRetailMovieAssets(EnbRetailAssetBundle assets)
    {
        _assets = assets; _cache = EnbW3dRenderResourceCache.ForAssetNamespace(assets);
        _animations = EnbRenderSession.Of(assets).MovieAnimations;
        foreach (var source in _cache.CapturePrototypeSources(out _).Values) _animations.Publish(source.Model);
    }
    public uint GetBoneIdentity(EnbRetailMovieModel model, string? bone)
    {
        var key = (model.Identity, model.BoneIndex(bone));
        if (!_boneIds.TryGetValue(key, out uint value)) _boneIds.Add(key, value = ++_nextBoneId);
        return value;
    }

    public EnbRetailMovieModel? CreateModel(int id, string name, bool ship)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 764060's generated ship state explicitly loads FILE=name.W3D before OBJECT=name.
        if (ship && _assets.W3dFiles.TryResolve(name + ".W3D", name, out var file, out var fileError))
        {
            _cache.TryPublishFile(file, out _, out _, out fileError);
            _animations.Publish(file.Model);
        }
        bool found = EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
            _assets.FileResources, _assets.MixResources, name, out var source, out var diagnostic,
            out var publications, _cache.CapturePrototypeSources(out _));
        foreach (var publication in publications)
        {
            if (!_cache.TryPublishFile(publication, out _, out _, out var publishError)) Diagnostic = publishError;
            _animations.Publish(publication.Model);
        }
        if (!found) { Diagnostic = diagnostic; return null; }
        // FILE construction replaces special-material/particle descriptors with
        // their retained owners. OBJECT must consume that published prototype.
        if (!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
            _assets.FileResources, _assets.MixResources, name, out source, out diagnostic,
            out _, _cache.CapturePrototypeSources(out _))) { Diagnostic = diagnostic; return null; }
        var compiledSource = source;
        if (source.CurrentRetailAggregate is { } aggregate)
            compiledSource = source with { LogicalFileName = aggregate.BaseLogicalFileName, RequestedObjectName = aggregate.BaseRequestedObjectName,
                ResourceLocation = aggregate.BaseResourceLocation, CurrentRetailAggregate = null, PrototypeFile = null,
                RegisteredPrototype = aggregate.BaseRegisteredPrototype, LooseFilePath = aggregate.BaseLooseFilePath };
        if (!_cache.TryGetOrCompile(compiledSource, out var resource, out var error)) { Diagnostic = error; return null; }
        string selected = source.CurrentRetailAggregate?.BaseRequestedObjectName ?? source.RequestedObjectName;
        string? hierarchyName = resource.HierarchyModels.FirstOrDefault(h => h.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))?.HierarchyName
            ?? resource.HierarchicalLods.FirstOrDefault(h => h.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))?.HierarchyName;
        if (hierarchyName is not null && !resource.Hierarchies.Any(h => h.Name.Equals(hierarchyName, StringComparison.OrdinalIgnoreCase)) &&
            _cache.TryResolveHierarchy(_assets.W3dFiles, hierarchyName, true, out var hierarchy, out error))
            resource = resource.Copy().SetHierarchies([.. resource.Hierarchies, hierarchy]);
        var model=new EnbRetailMovieModel(id, ++_identity, source, resource) { IsShip = ship };
        try
        {
            //804290 ->5B8120 ->5B8850 retains the constructed RenderObj
            //during the command, before any later command changes NAMEs.
            if(ModelFactory is {} create)
            {
                model.RenderObjectOwner=create(model);
                _modelOwners.Add(model);
            }
            return model;
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or NotSupportedException)
        {Diagnostic=ex.Message;return null;}
    }

    public void DestroyModel(EnbRetailMovieModel model)
    {
        //8046E0 ->5B8640 ->5B8850 releases the wrapper's RenderObj now.
        //Removing its scene entry alone leaves textures/mappers alive.
        _modelOwners.Remove(model);
        var owner=model.RenderObjectOwner;
        model.RenderObjectOwner=null;
        owner?.Dispose();
    }

    public void Animate(EnbRetailMovieModel model, string name, bool cycle, float start, float end, bool frozen)
    {
        var source = model.Source;
        var file = new EnbResolvedRslFileDependency(source.LogicalFileName, EnbRslFileDependencyStatus.ResolvedW3d,
            source.ResourceLocation, source.Model, source.Textures, LooseFilePath: source.LooseFilePath) { PrototypeFile = source.PrototypeFile };
        var animation = _animations.Resolve(model.Resource, file, _assets.Paths, _assets.MixResources, _assets.RetailMixResources, name);
        model.BindAnimation(animation, model.Resource, cycle, start, end, frozen);
        if (animation is null && _assets.W3dFiles.TryResolve(name + ".W3D", name, out var retry, out _))
        {
            _cache.TryPublishFile(retry, out _, out _, out _);
            _animations.Publish(retry.Model);
            // 5DCE22..43 loads and retries but discards the returned HAnim.
            _ = _animations.Resolve(model.Resource, file, _assets.Paths, _assets.MixResources, _assets.RetailMixResources, name);
        }
    }

    public bool Environment(string command, EnbRetailMovieArguments a)
    {
        switch (command)
        {
            case "CREATE_SKYBOX":
                if (Skybox is not null) return false;
                int skySelector = EnbRetailMovieScene.OptionalInt(a, 309);
                var skyDefinition = _assets.PresentationCatalog!.ResolveBaseAsset(skySelector);
                //80499B creates the object during the command, and53AC80 retains it.
                //Later movie commands may publish a different prototype before rendering.
                if(skyDefinition.Kind==EnbBaseAssetDefinitionKind.FileName ||
                    EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(skyDefinition))
                {
                    var selected=EnbCurrentRetailRObjEffectAsset.SelectOrdinaryBaseFactory(_assets,skySelector,
                        _cache.CreatePublicationContext(WallClock()),_cache.CapturePrototypeSources(out _));
                    Skybox=new(skySelector,selected.Source is null ? EnbBaseAssetResolutionStatus.NullObject :
                        EnbBaseAssetResolutionStatus.W3d,skyDefinition,selected.Source);
                }
                else Skybox = _assets.BaseAssetResolver?.Resolve(skySelector);
                return true;
            case "CREATE_STARS":
                if (Stars is not null) return false;
                int selector = EnbRetailMovieScene.OptionalInt(a, 800);
                StarConstructorArgument = EnbRetailMovieScene.OptionalInt(a, 1000);
                Stars = _assets.Stars?.ResolveLayers(selector) ?? EnbRetailStarCatalog.ParseText("").ResolveLayers(selector);
                //804BEE ->4829E0 ->481EF8 constructs every texture owner now,
                //including layers hidden by the eventual draw budget.
                foreach(var layer in Stars.Layers)
                    _starTextures.Add(string.IsNullOrEmpty(layer.Texture) ? null : _cache.AcquireNamedTexture(layer.Texture));
                return true;
            case "CREATE_ORBITAL":
                if (_orbitalCreated) return false;
                _orbitalCreated = true;
                if (!EnbRetailMovieScene.Int(a, out int orbital)) return false;
                if (!_assets.FileResources.TryRead($"orbital{orbital:D4}.ini", out var bytes, out _, out _)) return false;
                Orbitals = EnbRetailOrbitalSceneDefinition.Parse(EnbRetailIniDocument.Parse(Encoding.Latin1.GetString(bytes)));
                //804E9A ->4F34C0 ->4F2960 selects in authored order during the command.
                foreach(var entry in Orbitals.Orbitals)
                {
                    var definition = _assets.PresentationCatalog!.ResolveBaseAsset(entry.BaseAsset);
                    if(definition.Kind == EnbBaseAssetDefinitionKind.FileName || EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(definition))
                    {
                        try
                        {
                            _orbitalFactories.Add(entry, EnbCurrentRetailRObjEffectAsset.SelectOrdinaryBaseFactory(_assets,
                                entry.BaseAsset, _cache.CreatePublicationContext(WallClock()), _cache.CapturePrototypeSources(out _),
                                entry.Scale, new(entry.HsvH, entry.HsvS, entry.HsvV)));
                        }
                        catch(Exception error) when(error is InvalidDataException or NotSupportedException)
                        {
                            //Preserve per-orbital decoder containment; do not retry against a later namespace.
                            _orbitalFactoryFailures.Add(entry,error.Message);
                            Diagnostic = error.Message;
                        }
                    }
                }
                return true;
            default: return false;
        }
    }

    public bool Effect(string command, EnbRetailMovieArguments a, IReadOnlyList<EnbRetailMovieModel> models, bool preload, uint time)
    {
        if (!EnbRetailMovieScene.Int(a, out int sourceId)) return false;
        string? sourceBone = command is "CREATE_SFX_ON_BONE" or "CREATE_SFX_BETWEEN_BONES" ? a.Read() : null;
        bool binary = command.StartsWith("CREATE_SFX_BETWEEN", StringComparison.Ordinal);
        int targetId = 0;
        if (binary && !EnbRetailMovieScene.Int(a, out targetId)) return false;
        string? targetBone = command is "CREATE_SFX_BETWEEN_BONES" or "CREATE_SFX_BETWEEN_OBJECT_AND_BONE" ? a.Read() : null;
        if (!EnbRetailMovieScene.Int(a, out int definition)) return false;
        float durationMilliseconds = EnbRetailMovieScene.OptionalNumber(a, 0) * 1000;
        int duration = unchecked((int)durationMilliseconds);
        float scale = EnbRetailMovieScene.OptionalNumber(a, 1);
        var hsv = new Vector3(EnbRetailMovieScene.OptionalNumber(a, 0), EnbRetailMovieScene.OptionalNumber(a, 0), EnbRetailMovieScene.OptionalNumber(a, 0));
        int id = --_effectId;
        var resolved = _assets.PresentationCatalog?.FindEffect(definition);
        if (resolved is null) return false;
        // The preload factory owns the same resource acquisitions but never publishes an effect to the scene.
        if (preload)
        {
            if(resolved.EffectClassKind==EnbEffectClassKind.ParticleJetEffect)
            {
                try
                {
                    if(ParticleJetEffectPreloader is not {} construct)throw new InvalidDataException("Movie ParticleJet preload factory unavailable");
                    construct(resolved,scale,hsv);
                }
                catch(Exception ex) when(ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
                {Diagnostic=$"Movie ParticleJet preload E{definition}: {ex.Message}";}
                return false;
            }
            if(resolved.EffectClassKind==EnbEffectClassKind.PassEffect)
            {
                try
                {
                    if(PassEffectPreloader is not {} construct)throw new InvalidDataException("Movie PassEffect preload factory unavailable");
                    construct(resolved,hsv);
                }
                catch(Exception ex) when(ex is InvalidDataException or IOException or NotSupportedException)
                {Diagnostic=$"Movie PassEffect preload E{definition}: {ex.Message}";}
                return false;
            }
            if (resolved.UsesBaseAssetDirectly && resolved.BaseAssetId is { } asset)
            {
                try
                {
                    //8070C0 calls6E9490 before its preload/removal branch.
                    //6E9617 multiplies RObjScale;6E9642 adds RObjHSV, even
                    //when this temporary effect never enters the scene.
                    float authoredScale=1;
                    if(resolved.Properties.ContainsKey("RObjScale") &&
                        !EnbRetailIniDocument.TryReadFloat(resolved.Properties,"RObjScale",out authoredScale))
                        throw new InvalidDataException("Effect authored scale unavailable");
                    if(!EnbCurrentRetailEffectFactoryHsv.TryRead(resolved.Properties,hsv,out var factoryHsv,out var error,false))
                        throw new InvalidDataException(error);
                    var effectiveHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(factoryHsv)?factoryHsv:Vector3.Zero;
                    var prepared = RetainedObjectEffectFactory?.Invoke(resolved,authoredScale*scale,factoryHsv) ??
                        EnbCurrentRetailRObjEffectAsset.PrepareOwnedFactory(_assets, asset, effectiveHsv,
                        registeredPrototypes: _cache.CapturePrototypeSources(out _),factoryInputs:(authoredScale*scale,factoryHsv));
                    foreach (var publication in prepared.PrototypePublications)
                    {
                        _cache.TryPublishFile(publication, out _, out _, out _);
                        _animations.Publish(publication.Model);
                    }
                    var constructed=prepared.Build();
                    if(resolved.EffectClassKind is EnbEffectClassKind.RObjEffect or EnbEffectClassKind.RObjLookAtEffect or
                        EnbEffectClassKind.WarpEffect or EnbEffectClassKind.AttachEffect or EnbEffectClassKind.StraightSeekEffect or
                        EnbEffectClassKind.SpiralSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.LineEffect)
                        ObjectEffectPreloader?.Invoke(resolved,constructed,authoredScale*scale,factoryHsv);
                    //8070C0 discards only after6E9490 completes. Seek factories
                    //6EB330/6EB590 construct their random tables after the base
                    //asset succeeds, even without scene objects or activation.
                    if(resolved.EffectClassKind is EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect)
                    {
                        uint factoryDuration=EnbCurrentRetailEffectRslSchedulerRuntime.ResolveSeekConstructorDuration(resolved,duration);
                        _=EnbRetailSeekEffectConstruction.Spline(resolved,factoryDuration);
                        _=EnbRetailSeekEffectConstruction.Linear(resolved,factoryDuration);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
                { Diagnostic = ex.Message; }
            }
            return false; // 8070C0 marks the preload tree for removal and returns null.
        }
        if (durationMilliseconds > 0) EffectDeadline?.Invoke(id, unchecked(time + (uint)duration));
        bool binaryClass = resolved.EffectClassKind is EnbEffectClassKind.LineEffect or EnbEffectClassKind.RObjLookAtEffect
            or EnbEffectClassKind.ParticleJetEffect or EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SpiralSeekEffect
            or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect;
        if (resolved.EffectClassKind == EnbEffectClassKind.Unknown || binary != binaryClass) return false;
        var source = models.FirstOrDefault(m => m.Id == sourceId);
        var target = binary ? models.FirstOrDefault(m => m.Id == targetId) : null;
        bool bound = source is not null && (!binary || target is not null);
        var effect = new EnbRetailMovieEffect(id, definition, time, duration, scale, hsv, source, sourceBone, target, targetBone, binary);
        if (bound || command == "CREATE_SFX_BETWEEN_OBJECTS") _effects.Add(effect);
        return bound;
    }

    public bool Audio(string command, EnbRetailMovieArguments a, IReadOnlyList<EnbRetailMovieModel> models)
    {
        if (a.Read() is not { } name) return false;
        if (command == "STOP_AUDIO")
        {
            var sound = _sounds.FirstOrDefault(s => s.Attached && s.Name == name);
            if (sound is null) return false;
            sound.Stopped = true; return true;
        }
        int id = EnbRetailMovieScene.OptionalInt(a, -1);
        string? bone = a.Read();
        bool loop = EnbRetailMovieScene.OptionalInt(a, 0) != 0;
        float volume = EnbRetailMovieScene.OptionalNumber(a, 1);
        var request = new EnbRetailMovieSound(++_soundId, name, id != -1 && bone is not null,
            models.FirstOrDefault(m => m.Id == id), bone, loop, volume, false);
        _sounds.Add(request); return true;
    }
    public void Soundtrack(string name)
    {
        var sound = new EnbRetailMovieSound(++_soundId, name, false, null, null, false, 1, true);
        _sounds.Add(sound);
    }
    public void ExpireEffect(int id) => _effects.RemoveAll(e => e.Id == id);
    public void Damage(EnbRetailMovieModel model, int[] quadrants) => model.SetDamage(quadrants);
    public void Shake(float first, float second) => CameraShake.Add(first, second, WallClock());
    public void Update(uint milliseconds, Matrix4x4 camera) => OrbitalHours += milliseconds * .001f * BitConverter.UInt32BitsToSingle(0x3991a2b4);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _effects.Clear(); _sounds.Clear(); _boneIds.Clear(); Skybox = null; Stars = null; Orbitals = null;
        foreach(var texture in _starTextures)texture?.Dispose(); _starTextures.Clear();
        _orbitalFactories.Clear(); _orbitalFactoryFailures.Clear(); CameraShake.Clear(); EffectDeadline = null;
        foreach(var model in _modelOwners.ToArray())DestroyModel(model);ModelFactory=null;PassEffectPreloader=null;ParticleJetEffectPreloader=null;ObjectEffectPreloader=null;RetainedObjectEffectFactory=null;
    }
}

public sealed record EnbRetailMovieEffect(int Id, int Definition, uint Time, int Duration, float Scale, Vector3 Hsv,
    EnbRetailMovieModel? Source, string? SourceBone, EnbRetailMovieModel? Target, string? TargetBone, bool Binary);
public sealed record EnbRetailMovieSound(long Id, string Name, bool Attached, EnbRetailMovieModel? Model, string? Bone, bool Loop, float Volume, bool Music)
{
    public bool Stopped { get; internal set; }
}
