using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbNextControlsTests
{
    private static readonly EnbRetailIniDocument Definitions = EnbRetailIniDocument.Parse("""
        [Next Camera]
        Text=Next 3rd person camera
        Context=Camera
        PSW_1=F10
        [Rotate Camera]
        Text=Moves the camera around the ship
        Context=Camera
        KEY=Ctrl & mouse
        """);

    [Fact]
    public void HideInterfaceIsListedUnderCameraBeforeTheFixedRowsAndDefaultsToAltZ()
    {
        var bindings = EnbRetailKeyBindings.Resolve(Definitions, EnbRetailIniDocument.Parse(""), null);
        Assert.Equal(EnbNextControls.HideInterface, bindings.ResolveAction(k => k == "Z", EnbKeyModifiers.Alt));
        var editor = new EnbRetailControlOptions(Definitions, bindings);
        Assert.Equal(["Next Camera", EnbNextControls.HideInterface, "Rotate Camera"], editor.Definitions.Take(3).Select(d => d.Action));
        // Next rows of categories this map lacks follow at the end, in their own order.
        Assert.Equal(EnbNextControls.Definitions.Skip(1).Select(d => d.Action), editor.Definitions.Skip(3).Select(d => d.Action));
    }

    [Fact]
    public void TheDefaultStaysOffAChordTheRetailMapAlreadyUses()
    {
        var taken = EnbRetailIniDocument.Parse("""
            [Next Camera]
            Context=Camera
            PSW_1=@Z
            """);
        var bindings = EnbRetailKeyBindings.Resolve(taken, EnbRetailIniDocument.Parse(""), null);
        Assert.Equal("Next Camera", bindings.ResolveAction(k => k == "Z", EnbKeyModifiers.Alt));
        Assert.DoesNotContain(bindings.Bindings, b => b.Action == EnbNextControls.HideInterface);
    }

    [Fact]
    public void NextBindingsAreSavedInEnbNextsOwnFileNeverInTheRetailKeymap()
    {
        string directory = Path.Combine(Path.GetTempPath(), "enbnext-next-keys-" + Guid.NewGuid().ToString("N"));
        string? previous = EnbNextControls.Directory;
        EnbNextControls.Directory = directory;
        try
        {
            var editor = new EnbRetailControlOptions(Definitions, EnbRetailKeyBindings.Resolve(Definitions, EnbRetailIniDocument.Parse(""), 0x04030201));
            editor.Assign(EnbNextControls.HideInterface, 1, new(EnbNextControls.HideInterface, "F5", EnbKeyModifiers.None));
            string keymap = editor.SaveText("", 0x04030201);
            Assert.DoesNotContain("Next Hide", keymap, StringComparison.OrdinalIgnoreCase);
            EnbNextControls.Save(EnbRetailControlOptions.Section(0x04030201), (action, slot) => editor.Binding(action, slot)?.Encoded ?? "");
            var reload = EnbRetailKeyBindings.Resolve(Definitions, EnbRetailIniDocument.Parse(keymap), 0x04030201);
            Assert.Equal(EnbNextControls.HideInterface, reload.ResolveAction(k => k == "F5", 0));
            Assert.Null(reload.ResolveAction(k => k == "Z", EnbKeyModifiers.Alt));
        }
        finally { EnbNextControls.Directory = previous; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
