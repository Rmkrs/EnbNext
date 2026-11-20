using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class DamageTextTests
{
    [Theory]
    [InlineData(0, 0, "VictimDamageNormal")]
    [InlineData(0, 3, "VictimDamageCritical")]
    [InlineData(1, 3, "VictimDamageCritical")]
    [InlineData(2, 3, "VictimDamagePlasma")]
    [InlineData(3, 3, "VictimDamageCritical")]
    [InlineData(4, 3, "VictimDamageEMP")]
    [InlineData(5, 3, "VictimDamageChemical")]
    [InlineData(6, 3, "VictimDamageNormal")]
    public void TypeSelectsNumericColorIndependentlyOfCriticalHeader(uint type, uint inflicted, string style)
    {
        var outgoing = EnbDamageText.Resolve(new(196.7f, -4.8f, type, inflicted, 1, 2), 1)!.Value;
        var incoming = EnbDamageText.Resolve(new(196.7f, -4.8f, type, inflicted, 2, 1), 1)!.Value;
        Assert.Equal("197 (-4)", outgoing.Text);
        Assert.Equal(style, outgoing.Style);
        Assert.Equal(style, incoming.Style);
        Assert.Equal(type == 4, incoming.Reactor);
        Assert.True(incoming.Incoming);
        Assert.False(outgoing.Incoming);
        if (inflicted == 3)
        { Assert.Equal("Critical!", outgoing.Header); Assert.Equal("PlayerCriticalDamageHeader", incoming.HeaderStyle); }
    }

    [Fact]
    public void LocalFilterAndModifierTruncationMatchPacketHandler()
    {
        Assert.Null(EnbDamageText.Resolve(new(1, 0, 0, 0, 2, 3), 1));
        Assert.Equal("2 (+3)", EnbDamageText.Resolve(new(1.5f, 3.9f, 0, 0, 1, 2), 1)!.Value.Text);
        Assert.Equal("2", EnbDamageText.Resolve(new(1.5f, -.9f, 0, 0, 1, 2), 1)!.Value.Text);
        Assert.Equal("PlayerEMPHeader", EnbDamageText.Resolve(new(1, 0, 4, 0, 2, 1), 1)!.Value.HeaderStyle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void PacketBurstsSurviveCoalescingAndAcknowledgeDoesNotEraseNewerPackets(int unusedTailBytes)
    {
        var state = new Net7GameplayState(clientTime: () => 12345);
        void Receive(int amount)
        {
            byte[] bytes = new byte[24 + unusedTailBytes];
            bytes.AsSpan(24).Fill(0xAB);
            BinaryPrimitives.WriteSingleLittleEndian(bytes, amount);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 2);
            Assert.True(state.Apply(Packet.ForOpcode(0x64, bytes)));
        }
        var observed=new List<Net7DamagePresentation>();state.DamageObserved=observed.Add;
        for (int i = 0; i < 20; i++) Receive(i);
        var batch = state.DamageEvents.Pending;
        Assert.All(batch, entry => Assert.Equal(12345u, entry.ClientTime));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (float)i), batch.Select(e => e.Damage.Damage));
        Receive(20);
        state.DamageEvents.Acknowledge(batch[^1].Sequence);
        Receive(21);
        Assert.Equal(new float[] {20, 21}, state.DamageEvents.Pending.Select(e => e.Damage.Damage));
        Assert.Equal(Enumerable.Range(0,22).Select(i=>(float)i),observed.Select(d=>d.Damage));
        var captured = state.DamageEvents.Capture();
        var old = state.DamageEvents;
        state.ResetForServerHandoff();
        Assert.NotSame(old, state.DamageEvents);
        Assert.Empty(state.DamageEvents.Pending);
        Assert.Equal(2, captured.Pending.Count);
        var restored = System.Text.Json.JsonSerializer.Deserialize<Net7DamageStream>(System.Text.Json.JsonSerializer.Serialize(captured))!;
        Assert.Equal(captured.Pending, restored.Pending);
    }

    [Fact]
    public void EffectsRiseScaleFadeAndCompleteAtRetailCounterBoundary()
    {
        var style = new EnbInterfaceTextDefinition("test", EnbInterfaceDefinitionKind.String, "test",
            new Dictionary<string,string>{{"EffectOn", "fade,scroll,scale"}});
        var ini = EnbRetailIniDocument.Parse("[1]\nEffectID=fade\nEffectType=FADE\nInitial=1\nIntensity=0\nSpeed=2\n[2]\nEffectID=scroll\nEffectType=SCROLL\nDirection=UP\nSpeed=2\n[3]\nEffectID=scale\nEffectType=SCALE\nInitial=1\nIntensity=5\nSpeed=1");
        Assert.True(EnbDamageTextEffects.TryCreate(style, ini, out var effects));
        effects.Advance(500);
        Assert.Equal(1, effects.Opacity); Assert.Equal(0, effects.Scroll);
        effects.Advance(500);
        Assert.Equal(.5f, effects.Opacity); Assert.Equal(20, effects.Scroll); Assert.Equal(2, effects.Scale);
        Assert.False(effects.Complete);
        effects.Advance(16);
        Assert.Equal(0, effects.Opacity); Assert.True(effects.Complete); Assert.Equal(40, effects.Scroll);
    }
}
