using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldController
{
    /// <summary>Next Modern UI option "Hide corpses in space": hulks are left out of the drawn scene (their wreckage
    /// too: the world view skips them) and the gutter.</summary>
    internal bool HideCorpses { get => _hideCorpses; set { _hideCorpses = value; _w3dWorld.HideHulks = value; } }
    private bool _hideCorpses;

    private static bool HasCorpse(IReadOnlyList<IEnbRenderObject> objects)
    {
        for (int i = 0; i < objects.Count; i++) if (objects[i].ObjectType == 0x19) return true;
        return false;
    }

    private static IEnbRenderObject[] WithoutCorpses(IReadOnlyList<IEnbRenderObject> objects)
    {
        int kept = 0;
        for (int i = 0; i < objects.Count; i++) if (objects[i].ObjectType != 0x19) kept++;
        var result = new IEnbRenderObject[kept];
        for (int i = 0, n = 0; i < objects.Count; i++) if (objects[i].ObjectType != 0x19) result[n++] = objects[i];
        return result;
    }
}
