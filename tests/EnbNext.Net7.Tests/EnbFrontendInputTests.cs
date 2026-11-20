using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbFrontendInputTests
{
    [Theory]
    [InlineData("","")]
    [InlineData("kESTRAMOS","Kestramos")]
    [InlineData("abcdefghijklmnopqrstu","Abcdefghijklmnop")]
    public void NameNormalizationKeepsTheRetailSixteenCharacterBoundary(string value,string expected)
        =>Assert.Equal(expected,EnbFrontendUi.NormalizeCharacterName(value));

    [Fact]
    public void DisabledTransportCannotEditAndReenableDoesNotRestoreHeldModifiers()
    {
        using var editor=new EnbRetailTextEditor(20){Text="Ab",IsFocused=true};
        var input=new EnbUiTextInput(editor);int changed=0,lost=0;
        input.Changed+=(_,_)=>changed++;input.LostFocus+=(_,_)=>lost++;
        input.Key(new(0x11,0x200,0,0));input.IsEnabled=false;
        input.Key(new(8,0,0,0));input.TextInput("X");
        Assert.Equal("Ab",input.Text);Assert.Equal(0,changed);Assert.Equal(1,lost);Assert.False(input.IsFocused);
        input.IsEnabled=true;input.SetFocused(true);input.TextInput("c");
        Assert.Equal("Abc",input.Text);Assert.Equal(1,changed);
    }
}
