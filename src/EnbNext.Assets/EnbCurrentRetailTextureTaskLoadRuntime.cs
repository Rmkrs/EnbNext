using System.Numerics;

namespace EnbNext.Assets;

public enum EnbCurrentRetailTextureTaskLoadedSource { None,Dds,Tga }

/// <summary>0094A480's source dispatch after actual device levels are locked.
/// The caller supplies selected file bytes and incoming pixel/palette/device
/// storage. DDS whole-load success suppresses TGA even when no level writes.
/// The native caller advances task+9C to2 for every result, including None.</summary>
public static class EnbCurrentRetailTextureTaskLoadRuntime
{
}
