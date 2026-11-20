using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    // Render-thread publication only; no mutable session state is read here.
    // Gameplay consumes an immutable snapshot on its packet-processing thread.
    public void PublishRetainedEffectPositions(ImmutableDictionary<Net7EffectPoseKey, EnbNext.Net7.Gameplay.Net7RetainedEffectPosition> positions)
        => _net7?.Gameplay.PublishRetainedEffectPositions(positions);
}
