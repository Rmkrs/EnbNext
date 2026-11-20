using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private (uint Sector, long Generation)? _terrainKey;
    private Task<EnbRetailPreparedTerrain>? _terrainPreparation;
    private CancellationTokenSource? _terrainPreparationCancellation;
    public string? TerrainPrelightCacheDirectory {get;set;}
    private int _terrainMaximumWidth;
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
    private delegate void TerrainGetInteger(uint name,out int value);
    private void InitializeTerrainCapabilities(EnbGlApi gl)
    {
        var query=System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<TerrainGetInteger>(gl.GetProcAddress("glGetIntegerv"));
        query(0x0d33,out _terrainMaximumWidth); // GL_MAX_TEXTURE_SIZE, current context.
        if(_terrainMaximumWidth<=0)throw new InvalidOperationException("Terrain texture width capability unavailable.");
    }
    private IReadOnlyList<EnbW3dRenderMesh> _terrainMeshes = [];
    private EnbRetailTerrainSurface? _terrainSurface;
    private EnbRetailTerrainDisplacement? _terrainDisplacement;
    public bool TerrainDisplacementCapabilityEnabled { get; set; } = Environment.GetEnvironmentVariable("ENB_TERRAIN_DISPLACEMENT")=="1";
    public EnbRetailDisplacementSettings? TerrainDisplacementSettings { get; set; }
    private static EnbRetailDisplacementSettings ReadTerrainDisplacementSettings()
    {
        if(!OperatingSystem.IsWindows())return new();
        using var machine=Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,Microsoft.Win32.RegistryView.Registry32);
        using var key=machine.OpenSubKey(@"Software\Westwood Studios\Earth and Beyond\Render\DMap");
        int Read(string name,int fallback)=>key?.GetValue(name) is int value?value:fallback;
        return new(Read("DisplacementMappingEnabled",1)!=0,Read("BaseTessellationSize",16),Read("DisplacementMapSize",256),Read("TessellationRate",80000));
    }
    private IReadOnlyList<EnbW3dRenderMesh> _terrainDecorationMeshes = [];
    private int[] _terrainDecorationPatchIndices=[];
    private EnbW3dRenderMesh? _terrainWater;
    private EnbW3dRenderMesh? _terrainWaterSwatch;
    private EnbRetailTerrainPatch[] _terrainWaterPatches=[];
    private string _terrainStatus = "terrain inactive";
    private EnbRetailTerrain? _terrain;
    public EnbRetailTerrain? TerrainCameraData => _terrain;
    private EnbRetailTerrainPrelight? _terrainPrelight;
    private EnbTextureImage? _terrainColor, _terrainLitColor;
    private bool _terrainColorAlphaIsIllumination;
    private EnbRetailTerrainLightmap? _terrainLightmap;
    private string _terrainTextureName = "";
    private const ulong PlanetSkyIdentity = ulong.MaxValue - 9999;
    private const ulong PlanetBackgroundIdentity = ulong.MaxValue - 9998;
    // The +174 sea owner is independent of +18C decoration buffers. Its cache
    // identity must survive a decoration collection becoming empty or replaced.
    private const ulong PlanetWaterIdentity = ulong.MaxValue - 9997;
    private const ulong PlanetDisplacementIdentity = ulong.MaxValue - 9996;
    private (int Base, uint Sector, long Generation, ulong Transition, EnbBaseAssetResolution? Movie)? _planetSkyKey;
    private EnbW3dRenderResource? _planetSkyResource;
    private EnbW3dHlodSelection? _planetSkySelection;
    private bool _planetSkyOverbrightOff;
    private string? _planetSkyDiagnostic;
    private string _planetSkyStatus = "planet sky inactive";

    private EnbResolvedRenderCamera? AppendPlanetSkyPackets(LiveFrame frame, EnbRetailAssetBundle assets,
        EnbW3dLightEnvironment lighting, List<EnbW3dDrawTask> packets)
    {
        _planetSkyStatus = "planet sky inactive";
        var movieSky = frame.Movie?.Skybox;
        var environment = frame.Snapshot.Environment;
        if (movieSky is null && (!_useMainViewEnvironment ||
            !EnbCurrentRetailMainViewPassRuntime.RendersSkyScene(frame.Snapshot.IsTransitioning, environment?.PresentationMode) ||
            _influenceSkyPresentation.OverrideActive))
        {
            _planetSkyKey=null;_planetSkyResource=null;_planetSkySelection=null;
            return null;
        }
        int selector = movieSky?.Selector ?? environment!.SkyBaseAsset;
        var key = (selector, environment?.SectorNum ?? 0, _assetGeneration, frame.Snapshot.LocationTransition?.Version??0, movieSky);
        string? diagnostic = _planetSkyDiagnostic;
        if (_planetSkyKey != key)
        {
            _planetSkyKey = key;
            _planetSkyResource = null;
            _planetSkySelection = null;
            diagnostic=null;
            var definition=assets.PresentationCatalog!.ResolveBaseAsset(selector);
            _planetSkyOverbrightOff=definition.OverbrightOff;
            try
            {
                //513540 ->6F5150/6F51D0: registered/lazy NAME precedes FILE,
                //then NAME is retried and finally MISSING is acquired.53AC80
                //retains that result; later namespace publication cannot replace it.
                EnbResolvedW3dResource? source;
                if(movieSky is not null) source=movieSky.W3d;
                else if(definition.Kind==EnbBaseAssetDefinitionKind.FileName || EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(definition))
                {
                    var selected=EnbCurrentRetailRObjEffectAsset.SelectOrdinaryBaseFactory(assets,selector,
                        CreateLiveWorldPublicationContext(ReadMapperClock()),_prototypeResources.CapturePrototypeSources(out _));
                    //006F51D0's MISSING branch returns the MISSING prototype; 00513540
                    //installs that object in the sky scene like any other result.
                    source=selected.Source;
                }
                else source=assets.BaseAssetResolver!.Resolve(selector).W3d;
                if(source is not null && _prototypeResources.TryGetOrCompile(source,out _planetSkyResource,out diagnostic,ReadMapperClock()))
                {
                    var root=_planetSkyResource.HierarchicalLods.FirstOrDefault(candidate=>
                        string.Equals(candidate.Name,source.RequestedObjectName,StringComparison.OrdinalIgnoreCase));
                    if(root is {Levels.Count:>0})_planetSkySelection=new(root.Name,root.Levels[0].Ordinal);
                    else if(source.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh,Ordinal:var ordinal})
                        _planetSkyResource=_planetSkyResource.Copy().SetMeshes([_planetSkyResource.Meshes[ordinal]]);
                }
            }
            catch(Exception error) when(error is IOException or NotSupportedException)
            {diagnostic=error.Message;}
            _planetSkyDiagnostic=diagnostic;
        }
        if (_planetSkyResource is not { } resource)
        {
            _planetSkyStatus = $"sky rejected: BaseAsset {selector}: {diagnostic ?? "missing W3D resource"}";
            return null;
        }
        var obj = new EnbSceneObjectSnapshot(PlanetSkyIdentity, (ulong)_assetGeneration, 0, false, false,
            byte.MaxValue, selector, 1, 0, 0, 0, 0, false, "background sky", new(false, 0, null),
            new(EnbSceneSpatialProviderKind.Fixed,
                new(frame.CurrentClientTime, frame.Camera.Position, Quaternion.Identity,
                    Vector3.Zero, 0, Vector3.Zero, Vector3.UnitX), null),
            null, null, [], [], [], [], [], [], null, [], null, 0, 0);
        // MainView owns a separate sky light, enabled only by LightBackdrop.
        // Its ambient matches the ordinary scene; ordinary lights do not leak in.
        // 804800 creates a separate SceneClass, whose 9659B0 ambient .5 is
        // truncated to 127 by 917C80. Movie scene lights do not belong to it.
        var skyLighting = movieSky is not null ? EnbW3dLightEnvironment.CreateCurrentRetail(0x7f7f7f, []) : environment!.LightBackdrop ? lighting :
            EnbW3dLightEnvironment.CreateCurrentRetail(lighting.GlobalAmbientRgb24, []);
        if (!EnbW3dDrawPacketCompiler.TryBuild(obj, resource, frame.CurrentClientTime,
                EnbRenderPassKind.OrdinaryWorld, _planetSkySelection, skyLighting, out var draws, out diagnostic))
        {
            _planetSkyStatus = $"sky rejected: BaseAsset {selector}: {diagnostic}";
            return null;
        }
        var projection = frame.Camera.Projection;
        // Shared scene 0050D060: authored W3D with camera-centred translation.
        if (!EnbResolvedRenderCamera.TryCreate(frame.Camera.Position, frame.Camera.WorldOrientation,
                frame.Camera.Viewport, projection.HorizontalFieldOfViewRadians, projection.AspectRatio,
                new(20, 100000), out var camera)) return null;
        // 00513540 creates the sky through the same BaseAsset factory as world
        // objects, including its authored OverbrightOff-controlled mutation.
        foreach (var packet in draws.Packets)
            packets.Add(_overbrightMaterials.Apply(packet, _planetSkyOverbrightOff, out _));
        _planetSkyStatus = $"planet sky {resource.LogicalFileName} packets {draws.Packets.Count}";
        return camera;
    }

    private void AppendTerrainPackets(LiveFrame frame, EnbRetailAssetBundle assets,
        EnbW3dLightEnvironment lighting, List<EnbW3dDrawTask> packets,
        EnbRetailTerrainLightState? terrainLight, Dictionary<string, EnbTextureImage> runtimeTextures,
        IReadOnlyList<LiveRootInstance> roots,int landLod)
    {
        if (!_useMainViewEnvironment || frame.Snapshot.IsTransitioning ||
            frame.Snapshot.Environment is not { PresentationMode: 1 } environment)
        {
            _terrainPreparationCancellation?.Cancel();
            _terrainPreparation=null;_terrainKey=null;
            _terrain=null;_terrainDisplacement=null;
            _terrainStatus = "terrain inactive";
            return;
        }
        var key = (environment.SectorNum, _assetGeneration);
        if (_terrainKey != key)
        {
            _terrainKey = key;
            _terrainMeshes = [];
            _terrainSurface = null;
            _terrainDisplacement = null;
            _terrainDecorationMeshes = [];
            _terrainWater = null;
            _terrainWaterSwatch=null;_terrainWaterPatches=[];
            _terrain = null; _terrainPrelight = null; _terrainColor = null;
            _terrainLitColor = null; _terrainLightmap = null;
            _terrainPreparationCancellation?.Cancel();
            _terrainPreparationCancellation?.Dispose();
            _terrainPreparationCancellation=new();
            var cancellation=_terrainPreparationCancellation.Token;
            var resources=assets.RetailMixResources??assets.MixResources;
            if(resources is null){_terrainStatus="terrain rejected: Terrain resource catalog unavailable.";return;}
            var files=new EnbRetailFileResolver(Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!,resources);
            var prototypes=_prototypeResources;
            uint clock=ReadMapperClock();
            int maximumWidth=_terrainMaximumWidth;
            string installation=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(assets.Paths.ClientExecutablePath).ToUpperInvariant())));
            string cacheDirectory=TerrainPrelightCacheDirectory??Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EnbNext","TerrainPrelight",installation);
            _terrainPreparation=Task.Run(()=>EnbRetailTerrainPreparation.Prepare(environment.SectorNum,files,resources,
                prototypes,clock,maximumWidth,cacheDirectory,cancellation),cancellation);
            _=_terrainPreparation.ContinueWith(static task=>{_=task.Exception;},CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        if(_terrainPreparation is {} preparation)
        {
            if(!preparation.IsCompleted){_terrainStatus=$"terrain loading land{environment.SectorNum:D4}.ini";return;}
            _terrainPreparation=null;
            try
            {
                // Only the current sector/generation retains this task reference.
                // Worker completion never mutates render or camera state directly.
                var prepared=preparation.GetAwaiter().GetResult();
                var resources = assets.RetailMixResources ?? assets.MixResources
                    ?? throw new InvalidDataException("Terrain resource catalog unavailable.");
                EnbW3dRenderTextureLocation Texture(string name)
                {
                    if (assets.Paths.TryResolveRetailLooseResource(name, out string path))
                        return new(name, path, 0, new FileInfo(EnbNext.Installation.EnbPathCase.Resolve(path)).Length);
                    if (!resources.TryLocateResource(name, out var location))
                        throw new FileNotFoundException($"Terrain texture missing: {name}");
                    return new(location.Name, location.ArchivePath, location.DataOffset, location.DataLength);
                }
                string name = $"land{environment.SectorNum:D4}.ini";
                var terrain=prepared.Terrain;
                _terrain = terrain;
                _terrainPrelight=prepared.Prelight;
                _terrainColor=prepared.Color;
                _terrainColorAlphaIsIllumination=prepared.ColorAlphaIsIllumination;
                _terrainLightmap = new(terrain, _terrainPrelight, _terrainColor,
                    _terrainColorAlphaIsIllumination, terrain.LightmapChunk);
                _terrainTextureName = $"enbnext-land{terrain.Sector}-lit";
                _terrainSurface = new(terrain.Copy().SetColorMap(_terrainTextureName),
                    Texture(terrain.ColorMap), Texture(terrain.DetailMap));
                _terrainMeshes = _terrainSurface.Patches;
                string? displacementDiagnostic=null;
                if(TerrainDisplacementCapabilityEnabled)
                {
                    try
                    {
                        var displacement=TerrainDisplacementSettings??ReadTerrainDisplacementSettings();
                        if(displacement.Enabled)_terrainDisplacement=new(terrain,displacement,_terrainMeshes[0],_terrainMaximumWidth);
                    }
                    catch(Exception ex) when(ex is IOException or InvalidDataException or ArgumentException or OverflowException or System.Security.SecurityException or UnauthorizedAccessException or OutOfMemoryException)
                    {displacementDiagnostic=ex.Message;}
                }
                int decorationCount=0;
                string? decorationDiagnostic=null;
                try
                {
                    var decorations = prepared.Decorations??throw new InvalidDataException(prepared.DecorationDiagnostic??"Decoration preparation failed.");
                    var prototypes = new List<EnbRetailTerrainDecorationPrototype>();
                    var random=EnbRetailCrtRandomSource.Current;
                    using var factory=new EnbRetailTerrainShadowObjectFactory(resources,_prototypeResources,ReadMapperClock(),random.State,
                        new EnbRetailFileResolver(Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!,resources));
                    try
                    {
                        foreach (var prototype in decorations.Prototypes)
                        {
                            var root=factory.Create(prototype,out var reason);
                            if(root is null)throw new InvalidDataException(reason??$"Missing decoration prototype '{prototype}'.");
                            try{prototypes.Add(EnbRetailTerrainDecorationSelector.Select(root));}
                            finally{EnbRetailTerrainShadowObjectFactory.Release(root);}
                        }
                    }
                    finally{random.State=factory.CrtThreadSeed;}
                    _terrainDecorationMeshes = EnbRetailTerrainDecorationMesh.Build(terrain, decorations, prototypes,out _terrainDecorationPatchIndices);
                    decorationCount=decorations.Placements.Length;
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or NotSupportedException or FormatException or
                    OverflowException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                {
                    // 5137F0 retains its published terrain when47A2F0 cannot
                    // build optional decoration buffers. Keep failure local.
                    _terrainDecorationMeshes=[];decorationDiagnostic=ex.Message;
                }
                string? waterDiagnostic = null;
                if (terrain.HasSea)
                {
                    try
                    {
                        // Missing/non-mesh prototypes leave native +174 at zero.
                        if (EnbRetailTerrainWater.TryAcquireSwatch(_prototypeResources,assets.FileResources,resources,
                            terrain.SeaSwatch,ReadMapperClock(),out var mesh,out waterDiagnostic))
                        {
                            _terrainWater=EnbRetailTerrainWater.Build(terrain,mesh,[]);
                            _terrainWaterSwatch=mesh;
                        }
                        else waterDiagnostic ??= $"no mesh prototype '{terrain.SeaSwatch}'";
                    }
                    catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException or
                        OverflowException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                    {
                        waterDiagnostic = ex.Message;
                    }
                }
                _terrainStatus = $"terrain {name} {terrain.Width}x{terrain.Width} patches {_terrainMeshes.Count}";
                if(_terrainDisplacement is not null)_terrainStatus+="; displacement enabled";
                if(displacementDiagnostic is not null)_terrainStatus+=$"; displacement unavailable: {displacementDiagnostic}";
                _terrainStatus += prepared.Regenerated?"; prelight regenerated":"; prelight cache";
                _terrainStatus += $"; decorations {decorationCount} / {_terrainDecorationMeshes.Count} batches";
                if(decorationDiagnostic is not null)_terrainStatus += $"; decorations unavailable: {decorationDiagnostic}";
                if (waterDiagnostic is not null) _terrainStatus += $"; sea swatch unavailable: {waterDiagnostic}";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or FormatException or
                OverflowException or InvalidOperationException or ArgumentException or OperationCanceledException)
            {
                _terrain=null;_terrainPrelight=null;_terrainColor=null;_terrainLitColor=null;_terrainLightmap=null;
                _terrainSurface=null;_terrainDisplacement=null;_terrainMeshes=[];_terrainDecorationMeshes=[];
                _terrainWater=null;_terrainWaterSwatch=null;_terrainWaterPatches=[];
                _terrainStatus = ex is EnbRetailTerrainLoader.MissingHeightMapException
                    ? $"terrain absent: {ex.Message}" : $"terrain rejected: {ex.Message}";
            }
        }
        if (_terrainLightmap is {} lightmap && terrainLight is not null)
        {
            // 0046DA70 publishes only completed passes. The production GL
            // texture path uses RGBA8; retail's <=16-bit branch uses ARGB4444.
            if (lightmap.Update(terrainLight))
                _terrainLitColor = lightmap.CreateTexture(sixteenBit:false);
            runtimeTextures[_terrainTextureName] = _terrainLitColor!;
            _texturedBackend.RegisterRuntimeTexture(_terrainTextureName);
        }
        // 00474B30 submits height, the +174 sea passes, scene projectors,
        // then the +18C baked decoration buffers. Retain existing mesh identities.
        if(_terrainSurface is {} surface)
        {
            EnbW3dRenderMesh? displacedMesh=null;
            try{displacedMesh=_terrainDisplacement?.Evaluate(frame.Camera);}
            catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException or OutOfMemoryException)
            {
                _terrainDisplacement=null;
                _terrainStatus+=$"; displacement unavailable: {ex.Message}";
            }
            foreach(var draw in surface.Evaluate(frame.Camera,landLod))
            {
                if(displacedMesh is not null)continue;
                if(draw.Mesh.Indices.Count==0)continue;
                AppendMesh(draw.Mesh,ulong.MaxValue-10000-(ulong)draw.PatchIndex,draw.WorldTransform,draw.WorldBounds);
            }
            if(displacedMesh is {} mesh)
            {
                AppendMesh(mesh,PlanetDisplacementIdentity,Matrix4x4.Identity,new(mesh.BoundingSphereCenter,mesh.BoundingRadius),true);
            }
        }
        if(_terrainWaterSwatch is {} seaSwatch&&_terrain is {} seaTerrain&&_terrainSurface is {} seaSurface)
        {
            if(!_terrainWaterPatches.SequenceEqual(seaSurface.CurrentPatches))
            {
                _terrainWater=EnbRetailTerrainWater.Build(seaTerrain,seaSwatch,seaSurface.CurrentPatches);
                _terrainWaterPatches=seaSurface.CurrentPatches.ToArray();
            }
            if(_terrainWater is {} water)
                AppendMesh(water,PlanetWaterIdentity,
                    Matrix4x4.Identity,new(water.BoundingSphereCenter,water.BoundingRadius));
        }
        AppendSimpleTerrainShadowPackets(frame, assets, packets);
        if(_terrainSurface is {} decorationSurface)
        {
            for(int i=0;i<_terrainDecorationMeshes.Count;i++)
            {
                var patch=decorationSurface.CurrentPatches[_terrainDecorationPatchIndices[i]];
                // 46B3C0 copies the same visibility and level into +18C records;
                // 474B30 admits those records only below LOD3. Vertices stay world-space.
                if(!patch.Visible||(uint)patch.Level>=3)continue;
                var mesh=_terrainDecorationMeshes[i];
                if (mesh.Indices.Count == 0) continue;
                ulong identity = ulong.MaxValue - 10000 - (ulong)(_terrainMeshes.Count+i);
                AppendMesh(mesh,identity,Matrix4x4.Identity,new(mesh.BoundingSphereCenter,mesh.BoundingRadius));
            }
        }

        void AppendMesh(EnbW3dRenderMesh mesh,ulong identity,Matrix4x4 world,EnbW3dWorldBounds bounds,bool displacement=false)
        {
                foreach(var pass in mesh.MaterialPasses)
                {
                    var order = new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,
                        pass.SingleBindingSemantics?.FixedFunction.BlendEnabled == true ? EnbW3dDrawBlendClass.Blended : EnbW3dDrawBlendClass.Opaque,
                        EnbW3dStaticSortSemantics.FromAuthoredValue(0),
                        new($"land{environment.SectorNum}", assets.Paths.RootDirectory, 0, 0), mesh.Ordinal, pass.Ordinal, identity, 0);
                    packets.Add(new(order, identity, (ulong)_assetGeneration, 0, world,
                        bounds, mesh, null,
                        new(pass, pass.TriangleBindings[0].Semantics), lighting, frame.CurrentClientTime,
                        null, 0, mesh.Name){CurrentRetailTerrainSea=ReferenceEquals(mesh,_terrainWater),CurrentRetailTerrainDisplacement=displacement,CurrentRetailDepthBias=mesh.CurrentRetailDepthBias});
                }
        }
    }
}
