using EnbNext.Net7.Transport;

namespace EnbNext.Net7.Tests;

public sealed class Net7UdpSequenceStateTests
{
    [Fact]
    public void FixedSend_RepeatsLastNormalSequence_WithoutAdvancing()
    {
        var state = new Net7UdpSequenceState();
        Assert.Equal(0u, state.AllocateNormalSequence());
        Assert.Equal(1u, state.NextNormalSequence);
        Assert.Equal(0u, state.GetFixedSendSequence());
        Assert.Equal(1u, state.NextNormalSequence);
        Assert.Equal(1u, state.AllocateNormalSequence());
        Assert.Equal(1u, state.GetFixedSendSequence());
        Assert.Equal(2u, state.NextNormalSequence);
    }

    [Fact]
    public void FixedSendBeforeNormalSend_IsRejected()
    {
        var state = new Net7UdpSequenceState();
        Assert.Throws<InvalidOperationException>(() => state.GetFixedSendSequence());
    }

    [Fact]
    public void NormalSequence_WrapsModuloUint32()
    {
        var state = new Net7UdpSequenceState(uint.MaxValue);

        Assert.Equal(uint.MaxValue, state.AllocateNormalSequence());
        Assert.Equal(0u, state.NextNormalSequence);
        Assert.Equal(uint.MaxValue, state.GetFixedSendSequence());
        Assert.Equal(0u, state.AllocateNormalSequence());
        Assert.Equal(1u, state.NextNormalSequence);
    }

    [Fact]
    public void RecordedSequenceRestoresFixedSendAndWrapLineageOnlyIntoFreshState()
    {
        var original=new Net7UdpSequenceState(uint.MaxValue);original.AllocateNormalSequence();
        var restored=new Net7UdpSequenceState();restored.RestoreRecorded(original.Capture());
        Assert.Equal(uint.MaxValue,restored.GetFixedSendSequence());Assert.Equal(0u,restored.AllocateNormalSequence());
        Assert.Throws<InvalidOperationException>(()=>restored.RestoreRecorded(original.Capture()));
        Assert.Throws<InvalidDataException>(()=>new Net7UdpSequenceState().RestoreRecorded(new(3,1,true)));
    }
    [Fact]
    public void IndependentInstances_DoNotShareSequenceState()
    {
        var master = new Net7UdpSequenceState();
        var routed = new Net7UdpSequenceState();

        Assert.Equal(0u, master.AllocateNormalSequence());
        Assert.Equal(0u, routed.AllocateNormalSequence());
        Assert.Equal(1u, master.NextNormalSequence);
        Assert.Equal(1u, routed.NextNormalSequence);
    }
}
