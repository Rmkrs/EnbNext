using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>0045C430 constructs templates in authored state order, with
/// each insertion at the list head.0045C990 traverses that list when copying.
/// Other state constructors may load textures and must run in that sequence.</summary>
public sealed class EnbCurrentRetailRslEffectPassLibrary : IDisposable
{
    private readonly List<(int StateId, EnbCurrentRetailRslEffectPassOwner Owner)> _states = [];
    private bool _disposed;
    public IReadOnlyList<(int StateId, EnbCurrentRetailRslEffectPassOwner Owner)> States => _states;

    /// <summary>FILE calls surrounding00458C10 in the library factory:
    /// Animation0045ADF8 is unconditional; Object0045AB98 tests100;
    /// Bone0045BD4B tests literal8, independently of child precaching.</summary>
    public static bool PreloadsFile(EnbRslStateDefinition state) => state.Type switch
    {
        EnbRslStateType.Animation => true,
        EnbRslStateType.Object => state.Flags.Precached && !string.IsNullOrEmpty(state.Object),
        EnbRslStateType.Bone => state.Flags.ActivateNext && !string.IsNullOrEmpty(state.Object),
        _ => false,
    };

    public static EnbCurrentRetailRslEffectPassLibrary Create(EnbResolvedRslLibraryResourceGraph graph,
        EnbW3dRenderResourceCache resources, Action<EnbResolvedRslStateResource> constructOtherState)
    {
        var library = new EnbCurrentRetailRslEffectPassLibrary();
        try
        {
            foreach (var state in graph.States)
            {
                if (state.Definition.Type != EnbRslStateType.EffectPass)
                {
                    constructOtherState(state);
                    continue;
                }
                // Unsupported/failed optional state factories do not create a
                // replacement pass. Activation reports the unresolved state.
                if (state.FileDependency.EffectPass is not { ConstructsUsablePass: true, Texture.Length: > 0 } definition)
                    continue;
                library._states.Insert(0, (state.Definition.StateId,
                    EnbCurrentRetailRslEffectPassOwner.Create(resources, definition)));
            }
            return library;
        }
        catch { library.Dispose(); throw; }
    }

    public EnbCurrentRetailRslEffectPassLibrary Copy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var copy = new EnbCurrentRetailRslEffectPassLibrary();
        try
        {
            foreach (var (id, owner) in _states)
                copy._states.Insert(0, (id, owner.Copy()));
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    public EnbCurrentRetailRslEffectPassContext CreateContext(IEnumerable<int> activeStateIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = new EnbCurrentRetailRslEffectPassContext();
        foreach (int id in activeStateIds)
        {
            var state = _states.Find(state => state.StateId == id);
            if (state.Owner is not {} owner) continue;
            context.Apply(owner.Material, owner.Definition.TwoSided, owner.Definition.Hidden);
        }
        return context;
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var state in _states) state.Owner.Dispose();
        _states.Clear();
        _disposed = true;
    }
}
