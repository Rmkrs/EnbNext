using System.Text;
using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbTerrainDecorationFallbackTests
{
    [Theory]
    [InlineData(";[1309]\n;NUM_DECOS=10")]
    [InlineData("[1309]\nNUM_DECOS=0")]
    [InlineData("[1309]\nNUM_DECOS=-1")]
    [InlineData("[4195]\nNUM_DECOS=10")]
    public void AbsentCacheUsesAuthoredEmptyBranch(string ini)
    {
        var result = EnbRetailTerrainDecorations.Load(1309, name => name == "decoration.ini"
            ? Encoding.ASCII.GetBytes(ini) : throw new FileNotFoundException(name));
        Assert.True(result.UsesUndecoratedFallback);
        Assert.Empty(result.Prototypes);
        Assert.Empty(result.Placements);
    }

    [Fact]
    public void EmptyCacheStillConsultsGenerationConfiguration()
    {
        byte[] empty = [0xc0, 0xde, 0xc0, 0xde, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<NotSupportedException>(() => EnbRetailTerrainDecorations.Load(1309,
            name => name.EndsWith(".dcb") ? empty : Encoding.ASCII.GetBytes("[1309]\nNUM_DECOS=5000")));
    }

    [Fact]
    public void EmptyCacheRegeneratesWhileUnreadableResourcesRemainFailures()
    {
        var reads=new List<string>();
        var empty=EnbRetailTerrainDecorations.Load(1309,name=>{reads.Add(name);return [];});
        Assert.Equal(new[]{"land1309.dcb","decoration.ini"},reads);
        Assert.True(empty.UsesUndecoratedFallback);Assert.Empty(empty.Placements);
        var generated=new EnbRetailTerrainDecorations(["tree"],[]);
        int generations=0;
        Assert.Same(generated,EnbRetailTerrainDecorations.Load(1309,_=>[],()=>{generations++;return generated;}));
        Assert.Equal(1,generations);
        Assert.Throws<IOException>(() => EnbRetailTerrainDecorations.Load(1309, _ => throw new IOException("read failed")));
        Assert.Throws<FileNotFoundException>(() => EnbRetailTerrainDecorations.Load(1309, _ => throw new FileNotFoundException()));
    }

    [Fact]
    public void NonemptyCacheDoesNotReadGenerationConfiguration()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0xDEC0DEC0u); writer.Write(1u);
        var name = new byte[32]; Encoding.ASCII.GetBytes("tree").CopyTo(name, 0); writer.Write(name);
        writer.Write(1u); writer.Write(0u);
        foreach(float value in new[] {0f, 0, 0, 1, 10, 20, 1}) writer.Write(value);
        var result = EnbRetailTerrainDecorations.Load(1309, name => name.EndsWith(".dcb")
            ? stream.ToArray() : throw new InvalidOperationException("Unexpected INI read"));
        Assert.False(result.UsesUndecoratedFallback);
        Assert.Single(result.Placements);
    }
}
