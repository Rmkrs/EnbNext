namespace EnbNext.Rendering;

/// <summary>Draw-time context retained until the mesh renderer consumes it.
/// A static-sort enqueue does not retain this parent context.</summary>
public sealed record EnbCurrentRetailRslEffectPassSnapshot(uint Flags,
    System.Collections.Immutable.ImmutableArray<EnbW3dRenderMaterialPass?> Passes)
{
    public bool AdmitsBaseMesh(uint primaryShader)
        => (Flags & 4) == 0 || ((Flags & 8) != 0 &&
            ((primaryShader & 0x40000) != 0 || (primaryShader & 0xc000) == 0x8000));
}

/// <summary>00459080/004590F0 and00930FD0/00931010. A full material stack
/// suppresses additional passes but still pushes the effect's context flags.
/// Managed references retain the admitted passes until the corresponding pop.</summary>
public sealed class EnbCurrentRetailRslEffectPassContext(uint initialFlags = 0)
{
    private readonly List<EnbW3dRenderMaterialPass?> _passes = [];
    private readonly List<uint> _flags = [initialFlags];
    public IReadOnlyList<EnbW3dRenderMaterialPass?> Passes => _passes;
    public int SuppressedPassDepth { get; private set; }
    public int FlagDepth => _flags.Count - 1;
    public uint Flags => _flags[^1];
    public EnbCurrentRetailRslEffectPassSnapshot Snapshot()
        => new(Flags, System.Collections.Immutable.ImmutableArray.CreateRange(_passes));

    /// <summary>0093F4A4..4F2: context mask0x4 suppresses the ordinary base
    /// descriptor, except bit8 forces it for primary alpha-test/source-blend
    /// shaders. The shader belongs to the model's primary descriptor.</summary>
    public bool AdmitsBaseMesh(uint primaryShader)
        => (Flags & 4) == 0 || ((Flags & 8) != 0 &&
            ((primaryShader & 0x40000) != 0 || (primaryShader & 0xc000) == 0x8000));

    /// <summary>0093F53D..54C: mesh virtual+198 tests RenderObj bit4000.
    /// A pass whose +30 is true also admits such translucent meshes.</summary>
    public static bool AdmitsAdditionalMeshPass(bool meshTranslucent, bool passAdmitsTranslucent)
        => !meshTranslucent || passAdmitsTranslucent;

    public void PushPass(EnbW3dRenderMaterialPass? pass)
    {
        if (_passes.Count >= 31) SuppressedPassDepth++;
        else _passes.Add(pass);
    }

    public void Apply(EnbW3dRenderMaterialPass pass, bool twoSided, bool hidden)
    {
        PushPass(pass);
        _flags.Add(Flags | (twoSided ? 1u : 0) | (hidden ? 6u : 0));
    }

    public void Restore()
    {
        _flags.RemoveAt(_flags.Count - 1);
        if (SuppressedPassDepth != 0) SuppressedPassDepth--;
        else _passes.RemoveAt(_passes.Count - 1);
    }
}
