using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Retained MainView inputs (50..69) and outputs (6C..7C).
/// 50B360 zeroes inputs; 50C0D7/5146A0 read then force both dirty flags.
/// Ordinary 514480/5146D0 updates compare against retained, not last offered, inputs.
/// This is scene state and must survive device resource recreation.</summary>
public sealed class EnbRenderDetailOptionState
{
    private bool _initialized, _defaults;
    private float _gamma, _landPoly, _landDetail, _spacePoly, _textureSize;
    public EnbRenderDetailRuntimeSettings Current { get; private set; }
    public bool InputsChanged { get; private set; }
    public bool DefaultChanged { get; private set; }
    public bool Recomputed { get; private set; }

    public EnbRenderDetailRuntimeSettings Update(EnbRetailRenderBudgetSourceSettings sourceSettings,
        EnbRetailPlayerOptions options, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = sourceSettings.ClampLikeRetail();
        DefaultChanged = _defaults != options.GraphicsDefault;
        _defaults = options.GraphicsDefault;
        InputsChanged = Retain(ref _gamma, options.GraphicsGamma ?? 10f);
        InputsChanged |= Retain(ref _landPoly, options.GraphicsLandPoly ?? (source.LandPolyBudget - 8000) * 100f / 32000);
        InputsChanged |= Retain(ref _landDetail, options.GraphicsLandDetail ?? source.LandLod * 100f / 8);
        InputsChanged |= Retain(ref _spacePoly, options.GraphicsSpacePoly ?? (source.PolyBudget - 8000) * 100f / 32000);
        InputsChanged |= Retain(ref _textureSize, options.GraphicsTextureSize ?? 100f - source.TextureReduction * 100f / 3);
        if (!_initialized || force) InputsChanged = DefaultChanged = true;
        Recomputed = DefaultChanged || (!_defaults && InputsChanged);
        if (Recomputed)
            Current = EnbRenderDetailRuntimeSettings.FromRetailInputs(source, options with
            {
                GraphicsGamma = _gamma, GraphicsLandPoly = _landPoly, GraphicsLandDetail = _landDetail,
                GraphicsSpacePoly = _spacePoly, GraphicsTextureSize = _textureSize,
            });
        // Star budget/filter are independent machine inputs, not MainView's five floats.
        Current = Current with { StarBudget = source.StarBudget,
            Texture = EnbRenderTextureRuntimeSettings.FromRetailValues(Current.Texture.TextureReduction, source.TextureFilter) };
        _initialized = true;
        return Current;
    }

    private static bool Retain(ref float retained, float offered)
    {
        // Native FSUB -> single-precision spill -> abs -> ordered > 1.
        if (!(MathF.Abs(offered - retained) > 1f)) return false;
        retained = offered;
        return true;
    }
}
