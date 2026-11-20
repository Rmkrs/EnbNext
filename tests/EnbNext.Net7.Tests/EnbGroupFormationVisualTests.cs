using System.Collections.Immutable;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbGroupFormationVisualTests
{
    [Fact]
    public void FormationUsesRetailVariantsAndContainsUnboundedPositions()
    {
        // 00592C50: no group -> tsi03a_form0; Slot Back/Block/Pipe -> form1..3 with the
        // leader ship at its position and member ships; negative positions are skipped
        // and the six-entry array has no upper bound (safety rule: contain locally).
        var visual = new EnbGroupFormationVisual();
        visual.Rebuild(null);
        Assert.Equal("tsi03a_form0", visual.Asset);
        Assert.True(visual.Hidden);
        var group = new EnbSceneGroupSnapshot(true, "Block", 0,
            [new(0, "a", 1, 2), new(1, "b", 2, -1), new(2, "c", 3, 7)], null, true, ImmutableHashSet<uint>.Empty)
            { Formation = 1, AuxDataState = 2 };
        visual.Rebuild(group);
        Assert.Equal("tsi03a_form2", visual.Asset);
        Assert.Equal([("tsi03a_ShipLrg", 0), ("tsi03a_ShipSml", 2)], visual.Ships.Select(s => (s.Asset, s.Position)).ToArray());
        int selected = visual.Selected;
        visual.Select(7);
        Assert.Equal(selected, visual.Selected);
        visual.Select(2);
        Assert.Equal(2, visual.Selected);
        visual.Rebuild(group with { AuxDataState = 0 });
        Assert.Null(visual.Asset);
        Assert.Empty(visual.Ships);
    }
}
