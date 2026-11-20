using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// EnbNext-owned machine graphics baseline. Per-character graphics options
/// override these values, but the retired retail/E&amp;BConfig registry is not an
/// active configuration source.
/// </summary>
internal static class EnbNextRenderSettings
{
    public static EnbRetailRenderBudgetSourceSettings Current { get; set; } =
        EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults;
}
