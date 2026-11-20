namespace EnbNext.Rendering;

/// <summary>One consecutive material binding in an explicitly owned draw scope.
/// Retail 0097A298 changes the material pointer and marks 0x4000 only on a
/// replacement; 009254A0 consumes that bit before 00930910 applies mappers.
/// This is not a clock-keyed cache shared between materials or views.</summary>
public sealed class EnbW3dMaterialApplicationState
{
    private object? _material;
    private EnbW3dMapperRuntimeState? _stage0,_stage1;
    private ulong _revision0,_revision1;
    private uint _clock;
    private bool _matrix0,_matrix1;
    private EnbW3dMapperRuntimeEvaluation _value0,_value1;
    public long ApplicationCount { get; private set; }
    public long ReuseCount { get; private set; }
    public void Clear() { _material=null;_stage0=_stage1=null; }
    public void ResetCounters() { ApplicationCount=ReuseCount=0; }

    public void Evaluate(object? material,EnbW3dMapperRuntimeState stage0,EnbW3dMapperRuntimeState stage1,
        uint clock,bool matrix0,bool matrix1,
        out EnbW3dMapperRuntimeEvaluation value0,out EnbW3dMapperRuntimeEvaluation value1)
    {
        if(material is not null && ReferenceEquals(_material,material) &&
            ReferenceEquals(_stage0,stage0) && ReferenceEquals(_stage1,stage1) &&
            _clock==clock && _matrix0==matrix0 && _matrix1==matrix1 &&
            _revision0==stage0.StateRevision && _revision1==stage1.StateRevision)
        { value0=_value0;value1=_value1;ReuseCount++;return; }
        // Reset/evaluation through another owner, projection virtual selection,
        // or a new scope cannot reuse the previous device material application.
        value0=matrix0?stage0.EvaluateMatrix(clock):stage0.Evaluate(clock);
        value1=matrix1?stage1.EvaluateMatrix(clock):stage1.Evaluate(clock);
        _material=material;_stage0=stage0;_stage1=stage1;
        // Read both revisions after both stages: they can share one mapper.
        _revision0=stage0.StateRevision;_revision1=stage1.StateRevision;
        _clock=clock;_matrix0=matrix0;_matrix1=matrix1;_value0=value0;_value1=value1;
        ApplicationCount++;
    }
}
