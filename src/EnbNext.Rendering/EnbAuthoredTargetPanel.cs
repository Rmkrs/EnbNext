using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

/// <summary>Loads authored scanner geometry; production composition is GPU-only.</summary>
public static class EnbAuthoredTargetPanelLoader
{
    public static bool TryCreate(EnbRetailAssetBundle assets, out EnbAuthoredTargetPanel panel)
    {
        panel=null!;
        if(assets.RetailMixResources is not {} resources ||
            !resources.TryReadResource("tsi08a.w3d",out var bytes,out var location) ||
            !EnbW3dModel.TryDecode(bytes,out var model) ||
            !EnbAuthoredUiAsset.TryCompile(resources,location,model,"TSI08A",out var resource))return false;
        var hierarchy=model.Hierarchies.FirstOrDefault(h=>h.Name=="TSI08A");
        var scanner=resource.Meshes.FirstOrDefault(m=>m.Name=="SCANNER_CLICK");
        var pivot=hierarchy?.Pivots.FirstOrDefault(p=>p.Name=="SCANNER_CLICK");
        if(hierarchy is null || scanner is null || pivot is null ||
            !TryResolveTranslation(hierarchy,pivot.Ordinal,out var translation))return false;
        var size=scanner.BoundingBoxMax-scanner.BoundingBoxMin;
        panel=new(new EnbUiRect(translation.X-size.X/2+64,69-translation.Y-size.Y/2,size.X,size.Y),resource,hierarchy);
        return true;
    }
    public static bool TryResolveTranslation(EnbW3dHierarchy hierarchy, int ordinal, out Vector3 translation)
    {
        translation = Vector3.Zero;
        var visited = new HashSet<int>();
        int current = ordinal;
        while (current >= 0 && current < hierarchy.Pivots.Count && visited.Add(current))
        {
            EnbW3dPivot pivot = hierarchy.Pivots[current];
            if (!IsIdentity(pivot.Rotation))
                return false;
            translation += pivot.Translation;
            if (pivot.IsRoot)
                return true;
            current = checked((int)pivot.ParentIndex);
        }
        return false;
    }

    private static bool IsIdentity(EnbW3dRawQuaternion value)
        => MathF.Abs(value.Q0) <= 1e-5f && MathF.Abs(value.Q1) <= 1e-5f &&
           MathF.Abs(value.Q2) <= 1e-5f && MathF.Abs(value.Q3 - 1f) <= 1e-5f;

}
public sealed record EnbAuthoredTargetPanel(EnbUiRect ScannerBounds, EnbW3dRenderResource Resource, EnbW3dHierarchy Hierarchy);
