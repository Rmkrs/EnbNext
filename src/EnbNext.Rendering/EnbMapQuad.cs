using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbMapQuad(EnbUiRaster Texture,
    Vector3 A, Vector3 B, Vector3 C, Vector3 D,
    Vector2 UvA, Vector2 UvB, Vector2 UvC, Vector2 UvD, float Opacity = 1, bool SourceAlpha = false);
