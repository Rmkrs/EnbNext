using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Resolves the current-retail environment reflection selector. Retail passes
/// the SClient environment base asset to both the backdrop controller and the
/// reflection-texture builder; individual rendered-object assets do not select
/// BACK-%i.
/// </summary>
public static class EnbCurrentRetailEnvironmentMapRuntime
{
    public static bool TryResolveReflection(
        EnbEnvironmentMapCatalog? catalog,
        EnbSceneEnvironmentSnapshot? environment,
        out EnbEnvironmentReflectionDefinition definition,
        out string? diagnostic)
    {
        definition = default!;
        diagnostic = null;

        if (environment is null)
        {
            diagnostic = "scene environment is unavailable";
            return false;
        }

        if (catalog is null)
        {
            diagnostic = "installed envmaps.ini catalog is unavailable";
            return false;
        }

        if (!catalog.TryResolveReflection(environment.BaseAsset, out definition))
        {
            diagnostic = $"BACK-{environment.BaseAsset} and BACK-DEFAULT do not resolve a reflection texture";
            return false;
        }

        return true;
    }
}
