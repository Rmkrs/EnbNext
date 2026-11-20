using EnbNext.Assets;
using EnbNext.Net7.Timing;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z23ContinuousClockAndSecondHopTests
{
    [Fact]
    public void SessionClockRemainsContinuouslyReadableWithoutWorldPublication()
    {
        var clock = new ManualClock(1200);
        var session = new Net7Session(new Net7ServerOptions(), clock: clock);
        int publications = 0;
        session.PresentationStateChanged += _ => publications++;

        Assert.Equal(1200u, session.CurrentClientTime);
        clock.Now = 1475;
        Assert.Equal(1475u, session.CurrentClientTime);
        Assert.Equal(0, publications);
    }

    [Fact]
    public void SelectedRetailRslId2TopologyKeepsAuthoredNextAndSoundLinksDistinct()
    {
        EnbRslLibraryDefinition library = SelectedGateLibrary();
        Assert.Equal(7, library.States.Count);

        Assert.True(library.TryFindState(0, out EnbRslStateDefinition state0));
        Assert.Equal(EnbRslStateType.Object, state0.Type);
        Assert.Equal("tsg02", state0.Object);

        Assert.True(library.TryFindState(1, out EnbRslStateDefinition state1));
        Assert.Equal(EnbRslStateType.Animation, state1.Type);
        Assert.Equal("-1", state1.Suffix);
        Assert.Equal(2, state1.NextStateId);
        Assert.Equal(5, state1.LinkStateId);
        Assert.False(state1.Flags.ActivateNext);

        Assert.True(library.TryFindState(2, out EnbRslStateDefinition state2));
        Assert.Equal(EnbRslStateType.Animation, state2.Type);
        Assert.Equal("-2", state2.Suffix);
        Assert.Equal(3, state2.NextStateId);
        Assert.True(state2.Flags.ActivateNext);
        Assert.Null(state2.LinkStateId);

        Assert.True(library.TryFindState(3, out EnbRslStateDefinition state3));
        Assert.Equal(EnbRslStateType.Animation, state3.Type);
        Assert.Equal("-3", state3.Suffix);
        Assert.Equal(4, state3.NextStateId);
        Assert.Equal(6, state3.LinkStateId);
        Assert.False(state3.Flags.ActivateNext);

        Assert.True(library.TryFindState(4, out EnbRslStateDefinition state4));
        Assert.Equal(EnbRslStateType.Animation, state4.Type);
        Assert.Equal("-4", state4.Suffix);
        Assert.Equal(1, state4.NextStateId);
        Assert.True(state4.Flags.ActivateNext);

        Assert.True(library.TryFindState(5, out EnbRslStateDefinition state5));
        Assert.Equal(EnbRslStateType.Sound, state5.Type);
        Assert.Equal("TERRAN GATE SMALL IDLE", state5.Sound);
        Assert.True(state5.Flags.Precached);

        Assert.True(library.TryFindState(6, out EnbRslStateDefinition state6));
        Assert.Equal(EnbRslStateType.Sound, state6.Type);
        Assert.Equal("GATE ACTIVE IDLE", state6.Sound);
        Assert.True(state6.Flags.Precached);
    }

    private static EnbRslStateDefinition FindSelectedGateState(int stateId)
    {
        EnbRslLibraryDefinition library = SelectedGateLibrary();
        Assert.True(library.TryFindState(stateId, out EnbRslStateDefinition state));
        return state;
    }

    private static EnbRslLibraryDefinition SelectedGateLibrary()
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
            """
            [0\STATE0]
            ID=0
            TYPE=object
            OBJECT=placeholder

            [1\STATE0]
            ID=0
            TYPE=object
            OBJECT=placeholder

            [2\STATE0]
            ID=0
            TYPE=object
            OBJECT=tsg02

            [2\STATE1]
            ID=1
            TYPE=anim
            SUFFIX=-1
            NEXT=2
            LINK=5

            [2\STATE2]
            ID=2
            TYPE=anim
            SUFFIX=-2
            NEXT=3
            RS_ACTIVATE_NEXT=t

            [2\STATE3]
            ID=3
            TYPE=anim
            SUFFIX=-3
            NEXT=4
            LINK=6

            [2\STATE4]
            ID=4
            TYPE=anim
            SUFFIX=-4
            NEXT=1
            RS_ACTIVATE_NEXT=t

            [2\STATE5]
            ID=5
            TYPE=sound
            SOUND=TERRAN GATE SMALL IDLE
            RS_PRECACHED=true

            [2\STATE6]
            ID=6
            TYPE=sound
            SOUND=GATE ACTIVE IDLE
            RS_PRECACHED=true
            """);

        Assert.True(catalog.TryGetLibrary(2, out EnbRslLibraryDefinition library));
        return library;
    }

    private static EnbW3dRenderHierarchyAnimation Animation(string name, uint numFrames, uint frameRate)
        => new(
            Ordinal: 0,
            Version: 0x0003_0000u,
            Name: name,
            HierarchyName: "TSG02A",
            NumFrames: numFrames,
            FrameRate: frameRate,
            HasLegacySyntheticRootChannelShift: false,
            Channels: Array.Empty<EnbW3dRenderAnimationChannel>(),
            BitChannels: Array.Empty<EnbW3dRenderAnimationBitChannel>());

    private sealed class ManualClock(uint now) : INet7RuntimeClock
    {
        public uint Now { get; set; } = now;

        public uint GetMilliseconds() => Now;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
