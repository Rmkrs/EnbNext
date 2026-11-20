namespace EnbNext.Rendering;

/// <summary>An explicitly shared native mapper pointer. Definition equality
/// alone never creates sharing. A material-copy virtual creates a new owner.</summary>
public sealed class EnbW3dRetainedMapperOwner
{
    private readonly object _gate=new();
    private readonly EnbW3dRenderMapper? _definition;
    private EnbW3dMapperRuntimeState? _state;
    internal EnbW3dRetainedMapperOwner(EnbW3dRenderMapper definition)=>_definition=definition;
    internal EnbW3dRetainedMapperOwner(EnbW3dMapperRuntimeState state)=>_state=state;

    internal bool TryRetain(uint clock,EnbW3dRetailMapperRandomSource? random,
        out EnbW3dMapperRuntimeState state,out string? diagnostic)
    {
        lock(_gate)
        {
            diagnostic=null;
            if(_state is {} existing) {state=existing;return true;}
            state=null!;
            if(!EnbW3dMapperRuntimeProgramCompiler.TryCompile(_definition,out var program,out diagnostic) ||
                !EnbW3dMapperRuntimeState.TryCreate(program,clock,random,out state,out diagnostic))return false;
            _state=state;return true;
        }
    }
}
