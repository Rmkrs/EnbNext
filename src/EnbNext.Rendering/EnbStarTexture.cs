using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

// 00481EF8 -> manager+30/0091DE00, retained by PointGroup 00954830.
// Resolve at draw time, after the actual device's texture owner is configured.
public sealed class EnbStarTexture : IDisposable
{
    private readonly EnbRetailAssetBundle _assets;
    private readonly EnbCurrentRetailTextureRegistry.Lease _lease;
    public EnbW3dRenderTextureBinding Binding { get; }
    public string? Diagnostic { get; private set; }
    public EnbStarTexture(EnbRetailAssetBundle assets, string name, EnbCurrentRetailTextureRegistry.Lease? retained = null)
    {
        _assets = assets;
        _lease = retained?.Retain() ?? EnbW3dRenderResourceCache.ForAssetNamespace(assets).AcquireNamedTexture(name);
        Binding = new(0, name, null) { CurrentRetailTextureState = _lease.State, ResolveLogicalNameAtRuntime = true };
    }
    public EnbTextureImage? Resolve()
    {
        try
        {
            var texture = EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(
                _assets.Textures, Binding.LogicalName, Binding.CurrentRetailTextureState);
            // 0094968F/009494D4 publish the executable-authored shared failed-load
            // image. Unsupported managed decoders remain a local diagnostic.
            if (texture.Status == EnbTextureResolutionStatus.MissingResource)
                texture = _assets.Textures.ResolveCurrentRetailMissingTexture(Binding.LogicalName, _assets.Paths.ClientExecutablePath);
            Diagnostic = texture.Diagnostic;
            return texture.Image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            Diagnostic = $"Star texture {Binding.LogicalName}: {ex.Message}";
            return null;
        }
    }
    public void Dispose() => _lease.Dispose();
}
