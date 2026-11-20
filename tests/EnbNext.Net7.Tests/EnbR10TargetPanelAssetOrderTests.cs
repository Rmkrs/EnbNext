using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10TargetPanelAssetOrderTests
{
    private static readonly string[] VerbResources =
    [
        "VB_SCAN.W3D", "VB_LAND.W3D", "VB_LOOT.W3D", "VB_GROUP.W3D",
        "VB_MESSAGE.W3D", "VB_TRADE.W3D", "VB_TRACTOR.W3D", "VB_DOCK.W3D",
        "VB_PROSPECT.W3D", "VB_GATE.W3D", "VB_REGISTER.W3D", "VB_JUMPSTART.W3D",
        "VB_FOLLOW.W3D", "VB_DEF.W3D",
    ];

    [Fact]
    public void InstalledTsi08aRetainsNavigationSortAndBlendInputs()
    {
        EnbInstalledAssetPaths? paths = EnbInstalledAssetLocator.TryLocate();
        if (paths is null)
            return;

        EnbRetailAssetBundle assets = EnbRetailAssetBundle.Load(paths);
        EnbMixResourceCatalog resources = assets.RetailMixResources
            ?? throw new Xunit.Sdk.XunitException("Installed retail MIX resources were not loaded.");
        Assert.True(resources.TryReadResource(
            "TSI08A.W3D",
            out byte[] bytes,
            out EnbMixResourceLocation _));
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));

        EnbW3dHierarchy hierarchy = Assert.Single(
            model.Hierarchies,
            candidate => string.Equals(candidate.Name, "TSI08A", StringComparison.OrdinalIgnoreCase));
        Dictionary<string, int> pivots = hierarchy.Pivots.ToDictionary(
            static pivot => pivot.Name,
            static pivot => pivot.Ordinal,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, EnbW3dMesh> meshes = model.Meshes.ToDictionary(
            static mesh => mesh.Name,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(27, StaticSort(meshes["PREV_TARGET"]));
        Assert.Equal(27, StaticSort(meshes["NEXT_TARGET"]));
        Assert.Equal(25, StaticSort(meshes["SCANNER_CLICK"]));
        Assert.Equal(25, StaticSort(meshes["LEFT"]));
        Assert.Equal(25, StaticSort(meshes["RIGHT"]));
        Assert.True(pivots["SCANNER_CLICK"] < pivots["RIGHT"]);
        Assert.True(pivots["SCANNER_CLICK"] < pivots["LEFT"]);
        foreach (string glyph in new[] { "LEFT", "RIGHT" })
        {
            EnbW3dFixedFunctionState state = DecodeFirstShader(meshes[glyph]);
            Assert.True(state.BlendEnabled);
            Assert.Equal(EnbRenderBlendFactor.SourceAlpha, state.SourceBlend);
            Assert.Equal(EnbRenderBlendFactor.InverseSourceAlpha, state.DestinationBlend);
        }

        // Production draws the authored chrome through RenderGauges on the GPU.
        // ScannerProbe.GaugeProbe checks its actual ordered draw packets; the
        // removed CPU underlay/straight-alpha compositor is not a requirement.
    }

    [Fact]
    public void InstalledVerbsUseAlphaBaseAndAdditiveHoverClone()
    {
        EnbInstalledAssetPaths? paths = EnbInstalledAssetLocator.TryLocate();
        if (paths is null)
            return;

        EnbRetailAssetBundle assets = EnbRetailAssetBundle.Load(paths);
        EnbMixResourceCatalog resources = assets.RetailMixResources
            ?? throw new Xunit.Sdk.XunitException("Installed retail MIX resources were not loaded.");
        foreach (string resourceName in VerbResources)
        {
            Assert.True(resources.TryReadResource(
                resourceName,
                out byte[] bytes,
                out EnbMixResourceLocation _), resourceName);
            Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model), resourceName);

            EnbW3dFixedFunctionState[] shaders = model.Meshes
                .SelectMany(static mesh => mesh.Shaders)
                .Select(shader => DecodeShader(shader.Raw.Span))
                .ToArray();
            Assert.NotEmpty(shaders);
            Assert.All(shaders, state =>
            {
                Assert.True(state.BlendEnabled);
                Assert.Equal(EnbRenderBlendFactor.SourceAlpha, state.SourceBlend);
                Assert.Equal(EnbRenderBlendFactor.InverseSourceAlpha, state.DestinationBlend);
                Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
                    EnbCurrentRetailVerbPresentation.HoverShader(state.PackedRetailState),out var hover,out _));
                Assert.Equal(EnbRenderBlendFactor.One,hover.SourceBlend);
                Assert.Equal(EnbRenderBlendFactor.One,hover.DestinationBlend);
                Assert.Equal(state.TextureCombinerSemantics,hover.TextureCombinerSemantics);
            });
        }

    }

    [Fact]
    public void TargetOwnerRetainsUnchangedTextAndVerbIdentityAcrossSceneTicks()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var transform=new EnbSceneTransformSnapshot(0,Vector3.Zero,Quaternion.Identity,Vector3.Zero,0,Vector3.Zero,Vector3.UnitX);
        var target=new EnbSceneObjectSnapshot(1,1,1,true,true,0,175,1,0,0,0,0,false,"Scanner target",
            new(true,1000f,null),new(EnbSceneSpatialProviderKind.Fixed,transform,null),null,null,[],[],[],[],[],[],null,[],null,0,0);
        var scene=new EnbSceneSnapshot(1,0,null,null,null,false,
            new(0,false,0,[],null,100000,100000,false,false,100000,100000,1,0),
            new(null,null,false,null,null,null,null,false,[],1,new(1,1000000),[]),
            [],new(1,null,false,false){CurrentTargetVerbs=[0x50000]},new(0x333333,[]),[target],[],null,null,[],[]);
        var owner=new EnbScannerUi((_,_)=>Task.FromResult(false),(_,_,_)=>Task.FromResult(false));
        try
        {
            void Update()=>owner.Update(scene,EnbCurrentRetailRadarCompatibilityAdapter.Build(scene),assets,800,600);
            Update();var button=Assert.Single(owner.Verbs);var pixels=owner.Texts.Select(text=>text.Raster).ToArray();
            Assert.Contains(pixels,raster=>raster is not null);
            scene=scene with {Version=2};Update();
            Assert.Same(button,Assert.Single(owner.Verbs));
            for(int i=0;i<pixels.Length;i++)Assert.Same(pixels[i],owner.Texts[i].Raster);
            scene=scene with {Hud=scene.Hud with {CurrentTargetId=null}};Update();
            Assert.False(owner.VerbsVisible);Assert.All(owner.Texts,text=>Assert.Null(text.Raster));
            scene=scene with {Hud=scene.Hud with {CurrentTargetId=1,CurrentTargetVerbs=[0x50001]}};Update();
            Assert.False(Assert.Single(owner.Verbs).Executable);Assert.NotSame(button,owner.Verbs[0]);
        }
        finally {owner.ReleaseAssets();}
        Assert.Empty(owner.Verbs);Assert.All(owner.Texts,text=>Assert.Null(text.Raster));
    }

    private static int StaticSort(EnbW3dMesh mesh)
        => EnbW3dStaticSortSemantics.FromAuthoredValue(mesh.AuthoredSortLevel).RuntimeLevel;

    private static EnbW3dFixedFunctionState DecodeFirstShader(EnbW3dMesh mesh)
    {
        ReadOnlySpan<byte> raw = Assert.Single(mesh.Shaders).Raw.Span;
        return DecodeShader(raw);
    }

    private static EnbW3dFixedFunctionState DecodeShader(ReadOnlySpan<byte> raw)
    {
        var shader = new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(12, 4)));
        Assert.True(EnbW3dMaterialCompiler.TryDecodeShader(
            shader,
            out EnbW3dFixedFunctionState state,
            out string? diagnostic), diagnostic);
        return state;
    }

}
