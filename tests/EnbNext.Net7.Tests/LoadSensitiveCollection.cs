namespace EnbNext.Net7.Tests;

/// <summary>Tests with real-time network timeouts or exact allocation counts. They are
/// correct but fail when the machine is saturated by the parallel suite, so xUnit runs
/// this collection on its own after the parallel collections.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LoadSensitiveCollection
{
    public const string Name = "Load-sensitive timing and allocation";
}
