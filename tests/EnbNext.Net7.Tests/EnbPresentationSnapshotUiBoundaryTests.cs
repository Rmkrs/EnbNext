namespace EnbNext.Net7.Tests;

public sealed class EnbPresentationSnapshotUiBoundaryTests
{
    [Fact]
    // The UI never reads live network/session objects; the network lock boundary is
    // real (CURRENT_WORK owner-mutation map). The former positive assertions on the
    // removed per-frame snapshot accessor were dropped with that accessor.
    public void LiveWorldUiNeverReadsLiveNetworkState()
    {
        string view = Source("src/EnbNext.Engine/EnbWorldController.cs");
        string window = Source("src/EnbNext.Host.Sdl/SdlWorldScreen.cs");
        string mainWindow = Source("src/EnbNext.Host.Sdl/SdlWorldScreen.cs");
        string appProject = Source("src/EnbNext.App/EnbNext.App.csproj");
        string client = Source("src/EnbNext.Client/EnbClient.cs");

        Assert.Contains("BaseAssetReference", view, StringComparison.Ordinal);
        Assert.Contains("ClientBaseAssetReference", view, StringComparison.Ordinal);

        foreach (string forbidden in new[]
        {
            "Net7WorldState",
            "Net7WorldObject",
            "SystemNet7RuntimeClock",
            "TrySampleObject",
            "world.Objects",
            "_worldAccessor",
            "_clientBaseAssetAccessor",
            "EnbClientBaseAssetCatalog",
        })
        {
            Assert.DoesNotContain(forbidden, view, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, window, StringComparison.Ordinal);
        }

        Assert.Contains("()=>client()?.SamplePresentationSnapshot()", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_client?.World", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("public Net7WorldState? World", client, StringComparison.Ordinal);
        Assert.Contains("internal Net7WorldState? World", client, StringComparison.Ordinal);
        Assert.Contains("EnbNext.Presentation\\EnbNext.Presentation.csproj", appProject, StringComparison.Ordinal);
    }

    private static string Source(string relativePath)
    {
        string repo = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            string nested=Path.Combine(current.FullName,"EnbNext");
            if(File.Exists(Path.Combine(nested,"EnbNext.slnx")))return nested;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the EnbNext repository root.");
    }
}
