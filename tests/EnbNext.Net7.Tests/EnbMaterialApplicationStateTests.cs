using EnbNext.Rendering;
using System.Runtime.CompilerServices;
namespace EnbNext.Net7.Tests;

public sealed class EnbMaterialApplicationStateTests
{
    private static EnbW3dMapperRuntimeState Mapper()
    {
        var mapper=new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode04,"",1,1,.125f,-.25f,.2f,.4f,false);
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(mapper,out var program,out var error),error);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program,100,out var state,out error),error);
        return state;
    }
    [Fact]
    public void NativeCategorySequenceAppliesOnlyMaterialReplacements()
    {
        // Hardware receipt executes 0097A230 and 009254A0 from the pinned
        // client for A,A,B,B,A with two draw-state flushes per setter: 1,1,2,2,3.
        var state=new EnbW3dMaterialApplicationState();object a=new(),b=new();
        var m0=Mapper();var m1=Mapper();var literal0=Mapper();var literal1=Mapper();
        object[] sequence=[a,a,b,b,a];long[] expected=[1,1,2,2,3];
        EnbW3dMapperRuntimeEvaluation value0=default,value1=default;
        for(int i=0;i<sequence.Length;i++)
        {
            if(i==0 || !ReferenceEquals(sequence[i],sequence[i-1]))
            {value0=literal0.Evaluate(999);value1=literal1.Evaluate(999);}
            state.Evaluate(sequence[i],m0,m1,999,false,false,out var actual0,out var actual1);
            Assert.Equal(expected[i],state.ApplicationCount);
            Assert.Equal(value0,actual0);Assert.Equal(value1,actual1);
        }
        Assert.Equal(2,state.ReuseCount);
        state.Clear();state.Evaluate(a,m0,m1,1000,false,false,out var next,out _);
        Assert.Equal(literal0.Evaluate(1000),next);Assert.Equal(4,state.ApplicationCount);
    }
    [Fact]
    public void ClearingScopeReleasesMaterialAndMapperOwners()
    {
        var state=new EnbW3dMaterialApplicationState();var weak=Populate(state);
        state.Clear();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(weak.Item1.IsAlive);Assert.False(weak.Item2.IsAlive);GC.KeepAlive(state);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference,WeakReference) Populate(EnbW3dMaterialApplicationState state)
    {
        object material=new();var mapper=Mapper();state.Evaluate(material,mapper,mapper,1000,false,false,out _,out _);
        return(new(material),new(mapper));
    }
}
