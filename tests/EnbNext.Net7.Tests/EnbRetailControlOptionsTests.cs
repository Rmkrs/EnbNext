using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailControlOptionsTests
{
    private static readonly EnbRetailIniDocument Definitions = EnbRetailIniDocument.Parse("""
        [First]
        Text=First action
        Context=Targeting
        PSW_1=D
        PSW_2=
        SIM_1=W
        [Second]
        Text=Second action
        Context=Targeting
        PSW_1=C
        PSW_2=E
        SIM_1=R
        [-Disabled]
        Context=General
        PSW_1=B
        [Fixed]
        Context=General
        Text=Fixed action
        KEY=Esc
        """);
    private static EnbRetailControlOptions Editor() => new(Definitions, EnbRetailKeyBindings.Resolve(Definitions, EnbRetailIniDocument.Parse(""), null));

    [Fact]
    public void AlternateOnlySurvivesSaveReloadAndResolvesTheNewChord()
    {
        var edit = Editor();
        edit.Assign("First", 1, null);
        edit.Assign("First", 2, new("First", "F12", EnbKeyModifiers.Control));
        string saved = edit.SaveText("[Default]\nUser=old\n[old]\n1_First=Q\n[unrelated]\nData=keep\n", 0x04030201);
        var ini = EnbRetailIniDocument.Parse(saved);
        var reload = EnbRetailKeyBindings.Resolve(Definitions, ini, 0x04030201);
        var first = Assert.Single(reload.Bindings.Where(b => b.Action == "First"));
        Assert.Equal(2, first.Slot);
        Assert.Equal("First", reload.ResolveAction(k => k == "F12", EnbKeyModifiers.Control));
        Assert.Null(reload.ResolveAction(k => k == "D", 0));
        Assert.Equal("keep", ini.EnumerateSections().Single(s => s.Key == "unrelated").Value["Data"]);
        Assert.Equal("Q", ini.EnumerateSections().Single(s => s.Key == "old").Value["1_First"]);
        Assert.Equal("16909060", EnbRetailKeyBindings.Resolve(Definitions, ini, 0).SourceSection);
        Assert.Equal("16909060", reload.SourceSection);
    }
    [Fact]
    public void ConflictIsReadOnlyUntilAcceptedAndAcceptanceRemovesBothColumnDuplicates()
    {
        var edit = Editor();
        var candidate = new EnbRetailKeyBinding("First", "E", 0);
        Assert.Equal("Second", edit.Conflict("First", candidate)?.Action);
        Assert.Equal("D", edit.Binding("First", 1)?.Key);
        edit.Assign("First", 2, candidate);
        Assert.Null(edit.Binding("Second", 2));
        edit.Assign("First", 1, candidate);
        Assert.Null(edit.Binding("First", 2));
        Assert.Equal("E", edit.Binding("First", 1)?.Key);
    }
    [Fact]
    public void PresetsReplaceDraftAndFixedRowsAreNotEditable()
    {
        var edit = Editor();
        edit.Default(true); Assert.Equal("W", edit.Binding("First", 1)?.Key);
        Assert.Null(edit.Binding("Second", 2));
        edit.Default(false); Assert.Equal("D", edit.Binding("First", 1)?.Key);
        Assert.Equal("E", edit.Binding("Second", 2)?.Key);
        Assert.Throws<ArgumentException>(() => edit.Assign("Fixed", 1, null));
        Assert.Equal("Esc", edit.Definitions.Single(d => d.Action == "Fixed").FixedKey);
        Assert.DoesNotContain(edit.Definitions, d => d.Action.StartsWith('-'));
        Assert.DoesNotContain(edit.Draft.Bindings, b => b.Action.StartsWith('-'));
    }
}
