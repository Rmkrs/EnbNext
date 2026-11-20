using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlBackdropRenderer
{
    private EnbRenderTextureRuntimeSettings _starTextureSettings;
    private readonly bool _retainStarDeviceState=Environment.GetEnvironmentVariable("ENB_LEGACY_STAR_DEVICE_QUERIES")!="1";
    private GlInterface? _starCapabilityContext;
    private int _starMaximumTextureSize;
    private StarTextureParameterFloat? _starTextureParameterFloat;
    private EnbRetailAssetBundle? _starDeviceAssets;
    private EnbCurrentRetailOrdinaryTextureOwner? _starDeviceOwner;
    private Func<string,EnbCurrentRetailTextureRegistry.Lease>? _starAcquireTexture;
    private int _starDeviceReduction;
    private long _starDeviceTextureRevision;
    internal long StarCapabilityQueries {get;private set;}
    private void InvalidateStarDeviceState()
    {
        _starCapabilityContext=null;_starMaximumTextureSize=0;_starTextureParameterFloat=null;
        _starDeviceAssets=null;_starDeviceOwner=null;_starAcquireTexture=null;
    }
    private EnbRenderTextureBackendCapabilities _starTextureCapabilities;
    private readonly Dictionary<EnbStarTexture, (int Id, EnbTextureImage Image)> _starTextures = new();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void StarTextureParameterFloat(int target, int parameter, float value);

    public void ConfigureStarTextures(GlInterface gl, EnbRetailAssetBundle assets,
        EnbRenderTextureRuntimeSettings settings)
    {
        _starTextureSettings = settings;
        // Capabilities and entry points belong to the GL context, not a frame.
        // Deinitialize also invalidates them for a replacement native context
        // even if the managed GlInterface object is reused.
        if(!_retainStarDeviceState || !ReferenceEquals(_starCapabilityContext,gl))
        {
            _starTextureCapabilities = new(EnbOpenGlStage0TextureBackend.SupportsAnisotropicFiltering(gl.ContextInfo.Extensions));
            LoadRequiredProc<BackdropGetInteger>(gl, "glGetIntegerv")(0x0D33, out int maximum);
            StarCapabilityQueries++;
            if(maximum<=0)throw new InvalidOperationException("Texture width capability unavailable.");
            _starMaximumTextureSize=maximum;_starCapabilityContext=gl;
            _starTextureParameterFloat=null;_starDeviceOwner=null;
        }
        if(_starDeviceOwner is null || !ReferenceEquals(_starDeviceAssets,assets) || _starDeviceReduction!=settings.TextureReduction || _starDeviceTextureRevision!=assets.TextureRevision)
        {
            const uint formats = (1u<<1)|(1u<<2)|(1u<<3)|(1u<<4)|(1u<<6)|(1u<<7)|(1u<<9)|(1u<<14)|(1u<<20)|(1u<<21)|(1u<<22)|(1u<<23)|(1u<<24);
            _starDeviceOwner=new(new((uint)_starMaximumTextureSize,(uint)_starMaximumTextureSize,formats,true),32,32,settings.TextureReduction);
            _starAcquireTexture=EnbW3dRenderResourceCache.ForAssetNamespace(assets).AcquireNamedTexture;
            _starDeviceAssets=assets;_starDeviceReduction=settings.TextureReduction;
            // The source revision retires its registry; never republish the old acquire callback.
            _starDeviceTextureRevision=assets.TextureRevision;
        }
        // Other views may publish another reduction against this texture
        // resolver. Restore the current view's owner on every invocation.
        EnbCurrentRetailEffectMeshHsvRuntime.SetOrdinaryTextureDevice(assets.Textures,_starDeviceOwner,_starAcquireTexture);
    }

    private bool BindStarTexture(GlInterface gl, EnbStarTexture source)
    {
        if (source.Resolve() is not {} image) return false;
        RequireProc(_activeTexture, "glActiveTexture")(GlTexture0);
        if (_starTextures.TryGetValue(source, out var retained) && !ReferenceEquals(retained.Image, image))
        {
            int old = retained.Id; RequireProc(_deleteTextures, "glDeleteTextures")(1, ref old);
            _starTextures.Remove(source);
        }
        if (!_starTextures.TryGetValue(source, out retained))
        {
            RequireProc(_genTextures, "glGenTextures")(1, out int texture);
            RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, texture);
            RequireProc(_pixelStorei, "glPixelStorei")(GlUnpackAlignment, 1);
            for (int level = 0; level < image.MipLevels.Count; level++)
            {
                var mip = image.MipLevels[level];
                if (!mip.CompressedBlocks.IsEmpty) { UploadCompressedTexture(mip, level); continue; }
                var pixels = mip.Pixels.Span;
                byte[] rgba = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                { rgba[i*4]=pixels[i].R; rgba[i*4+1]=pixels[i].G; rgba[i*4+2]=pixels[i].B; rgba[i*4+3]=pixels[i].A; }
                unsafe { fixed (byte* data = rgba) RequireProc(_texImage2D, "glTexImage2D")(
                    GlTexture2D, level, GlRgba, mip.Width, mip.Height, 0, GlRgba, GlUnsignedByte, new IntPtr(data)); }
            }
            if (EnbOpenGlStage0TextureBackend.SupportsTextureMaximumLevel(gl.ContextInfo.Version))
                RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, 0x813D, image.MipLevels.Count - 1);
            retained = (texture, image); _starTextures.Add(source, retained);
        }
        RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, retained.Id);
        var sampler = EnbW3dTextureSamplerCompiler.Compile(source.Binding, _starTextureSettings, _starTextureCapabilities);
        var parameter = RequireProc(_texParameteri, "glTexParameteri");
        parameter(GlTexture2D, GlTextureMinFilter, EnbOpenGlStage0TextureUploadPlanner.MapMinFilter(sampler, image.MipLevels.Count));
        parameter(GlTexture2D, GlTextureMagFilter, sampler.MagFilter == EnbRenderTextureMinMagFilter.Point ? 0x2600 : GlLinear);
        parameter(GlTexture2D, GlTextureWrapS, EnbOpenGlStage0TextureUploadPlanner.MapAddressMode(sampler.AddressU));
        parameter(GlTexture2D, GlTextureWrapT, EnbOpenGlStage0TextureUploadPlanner.MapAddressMode(sampler.AddressV));
        if (_starTextureCapabilities.SupportsAnisotropicFiltering)
            (_retainStarDeviceState ? _starTextureParameterFloat ??= LoadRequiredProc<StarTextureParameterFloat>(gl,"glTexParameterf") : LoadRequiredProc<StarTextureParameterFloat>(gl,"glTexParameterf"))(GlTexture2D, 0x84FE, sampler.MaximumAnisotropy);
        return true;
    }

    private void RetireStarTextures(GlInterface? gl, EnbBackdropFrame? frame)
    {
        foreach (var (source, texture) in _starTextures.ToArray())
        {
            if (frame is not null && frame.Stars.Any(layer => ReferenceEquals(layer.StarTexture, source))) continue;
            int id = texture.Id;
            if (gl is not null) RequireProc(_deleteTextures, "glDeleteTextures")(1, ref id);
            _starTextures.Remove(source);
        }
    }
}
