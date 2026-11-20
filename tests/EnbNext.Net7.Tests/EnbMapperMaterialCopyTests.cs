using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMapperMaterialCopyTests
{
    private static EnbW3dMapperRuntimeProgram Program(int selector, string args = "")
    {
        var mapper = new EnbW3dRenderMapper((EnbW3dMapperTypeSelector)selector, args,
            2, 3, .25f, -.5f, .375f, .625f, false);
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(mapper, out var program, out var error), error);
        return program;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    [InlineData(14)] [InlineData(15)] [InlineData(18)] [InlineData(19)] [InlineData(20)]
    public void CopyRestartsPhaseAndKeepsAuthoredParameters(int selector)
    {
        var program = Program(selector, "FPS=-4\nLog2Width=2\nLast=8\nOffset=1\nSpeed=.25\nUAmplitude=.2\nUFrequency=1\nUStep=.1\nSPS=2\nPeriod=2\nBumpRotation=.2\nBumpScale=1.5\nAxis=X");
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 10, out var source, out var error), error);
        var initial = source.Evaluate(10);
        source.Evaluate(510);
        var copy = source.CloneForMaterial(1000);
        Assert.NotSame(source, copy);
        Assert.Equal(program, copy.Program);
        Assert.Equal(1000u, copy.InitialMilliseconds);
        Assert.Equal(initial, copy.Evaluate(1000));
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 1000, out var independent, out error), error);
        Assert.Equal(independent.Evaluate(1250), copy.Evaluate(1250));
    }

    [Fact]
    public void EnvironmentCopyPreservesCurrentVButStartsANewClock()
    {
        var program = Program(17, "VPerSec=.25\nVStart=.125\nUseReflect=1");
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 0, out var source, out var error), error);
        var advanced = source.Evaluate(500);
        var copy = source.CloneForMaterial(900);
        Assert.Equal(advanced, copy.Evaluate(900));
        Assert.Equal(source.Evaluate(750), copy.Evaluate(1150));
    }

    [Fact]
    public void RandomCopyConsumesFreshTripleFromSourceStream()
    {
        var program = Program(16, "FPS=0");
        var random = new EnbW3dRetailMapperRandomSource();
        var controlRandom = new EnbW3dRetailMapperRandomSource();
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 0, random, out var source, out var error), error);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 0, controlRandom, out var first, out error), error);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 700, controlRandom, out var second, out error), error);
        var copy = source.CloneForMaterial(700);
        Assert.Equal(first.Evaluate(0), source.Evaluate(0));
        Assert.Equal(second.Evaluate(700), copy.Evaluate(700));
        Assert.NotEqual(source.Evaluate(0), copy.Evaluate(700));
        Assert.Equal(controlRandom.NextUInt32(), random.NextUInt32());
    }

    [Fact]
    public void UniqueModelCopiesEachMaterialMapperAndRetiresOnlyTheLastOwner()
    {
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(Program(4), 0, out var mapper, out var error), error);
        object a = new(), b = new();
        var retired = new List<object>();
        using var prototype = new EnbCurrentRetailMeshModelOwner([(a, mapper, null), (b, mapper, null)], retired.Add);
        using var instance = prototype.CloneRenderObject();
        var initial = mapper.Evaluate(0);
        mapper.Evaluate(500);
        instance.EnsureUniqueModel(500);
        var copyA = Assert.IsType<EnbW3dMapperRuntimeState>(instance.MapperIdentity(a, 0));
        var copyB = Assert.IsType<EnbW3dMapperRuntimeState>(instance.MapperIdentity(b, 0));
        Assert.NotSame(copyA, copyB);
        Assert.Equal(initial, copyA.Evaluate(500));
        Assert.Same(mapper, prototype.MapperIdentity(a, 0));
        Assert.Empty(retired);
        var unique = instance.ModelIdentity;
        instance.EnsureUniqueModel(900);
        Assert.Same(unique, instance.ModelIdentity);
        instance.EnsureUniqueModel(900, force: true);
        Assert.Same(unique, Assert.Single(retired));
        Assert.Equal(900u, Assert.IsType<EnbW3dMapperRuntimeState>(instance.MapperIdentity(a, 0)).InitialMilliseconds);
    }
}
