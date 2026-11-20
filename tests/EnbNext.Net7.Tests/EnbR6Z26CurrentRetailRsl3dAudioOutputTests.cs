using System.Buffers.Binary;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z26CurrentRetailRsl3dAudioOutputTests
{
    [Fact]
    public void BoundedCurrentRetailType1SoundMapsToBackendNeutral3dPlan()
    {
        EnbRetailSoundDefinition definition = ParseSingle(
            """
            [Gate Active]
            SoundFilename=gate.wav
            SoundVolume=0.5
            SoundPriority=1
            SoundMaxVolRadius=4000
            SoundDropOffRadius=10000
            SoundMaxInstances=4
            SoundPitch=1
            """);
        EnbRetailSoundResolution resolution = ExactLooseResolution(definition);
        EnbCurrentRetailRslSoundContract contract = EnbCurrentRetailRslSoundRuntime.Describe(Flags());

        Assert.True(EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(
            definition,
            resolution,
            contract,
            out EnbCurrentRetailRsl3dSoundPlan plan,
            out EnbRetailSoundResolvedChoice? selected,
            out string? diagnostic), diagnostic);

        Assert.NotNull(selected);
        Assert.Equal(0.5f, plan.Gain);
        Assert.Equal(1f, plan.Pitch);
        Assert.Equal(4000f, plan.ReferenceDistance);
        Assert.Equal(10000f, plan.MaximumDistance);
        Assert.True(plan.LoopContinuously);
        Assert.True(plan.FollowsLiveObjectTransform);
        Assert.True(plan.StopsOnCleanup);
        Assert.Equal(4, plan.MaximumInstances);
        Assert.Equal(1, plan.SoundType);
        Assert.Equal(1f, plan.Priority);
    }

    [Fact]
    public void RetailCloneMaskDropsStationaryBeforeTheLiveSoundUpdateSoTheInstanceStillFollows()
    {
        EnbRetailSoundDefinition definition = ParseSingle(
            """
            [Stationary]
            SoundFilename=stationary.wav
            """);
        EnbCurrentRetailRslSoundContract contract =
            EnbCurrentRetailRslSoundRuntime.Describe(Flags(stationary: true));

        Assert.NotEqual(0u, contract.ParsedFlags & 0x0000_4000u);
        Assert.Equal(0u, contract.InstanceFlags & 0x0000_4000u);
        Assert.True(EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(
            definition,
            ExactLooseResolution(definition),
            contract,
            out EnbCurrentRetailRsl3dSoundPlan plan,
            out _,
            out string? diagnostic), diagnostic);
        Assert.True(plan.FollowsLiveObjectTransform);
    }

    [Fact]
    public void PlanRequiresResolvedChoiceButPreservesDetachedSoundLifetime()
    {
        EnbRetailSoundDefinition multi = ParseSingle(
            """
            [Random]
            [Random\Files]
            a.wav=2
            b.wav=1
            """);
        var multiResolution = new EnbRetailSoundResolution(
            multi,
            [
                ExactChoice("a.wav", weight: 2),
                ExactChoice("b.wav", weight: 1),
            ]);
        Assert.False(EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(
            multi,
            multiResolution,
            EnbCurrentRetailRslSoundRuntime.Describe(Flags()),
            out _, out _, out string? multiDiagnostic));
        Assert.Contains("random-selection", multiDiagnostic);

        EnbRetailSoundDefinition dontStop = ParseSingle("[Keep]\nSoundFilename=keep.wav\n");
        Assert.True(EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(
            dontStop,
            ExactLooseResolution(dontStop),
            EnbCurrentRetailRslSoundRuntime.Describe(Flags(dontStopSound: true)),
            out var detachedPlan, out _, out string? cleanupDiagnostic),cleanupDiagnostic);
        Assert.False(detachedPlan.StopsOnCleanup);

    }

    [Theory]
    [InlineData("SoundMinVolumeShift=0.1", "volume/pitch")]
    [InlineData("SoundSecsBetweenCalls=1", "SecondsBetweenCalls")]
    public void FirstAudibleSliceRejectsUnprovenDefinitionBehavior(string authoredLine, string diagnosticFragment)
    {
        EnbRetailSoundDefinition definition = ParseSingle(
            $"[Bounded]\nSoundFilename=bounded.wav\n{authoredLine}\n");

        Assert.False(EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(
            definition,
            ExactLooseResolution(definition),
            EnbCurrentRetailRslSoundRuntime.Describe(Flags()),
            out _, out _, out string? diagnostic));
        Assert.Contains(diagnosticFragment, diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    public void RslFactoryPassesCategoryAndIgnoresDopplerLimits(int category)
    {
        var definition = ParseSingle($"[Sound]\nSoundFilename=sound.wav\nSoundType={category}\nSoundMinDopplerShift=0.5\nSoundMaxDopplerShift=2\n");
        Assert.True(EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(
            definition, ExactLooseResolution(definition),
            EnbCurrentRetailRslSoundRuntime.Describe(Flags()),
            out var plan, out _, out var diagnostic), diagnostic);
        Assert.Equal(category, plan.SoundType);
        Assert.Equal(1f, plan.Pitch);
    }

    [Fact]
    public void ManagedWaveDecoderReadsPcm16AcrossOrdinaryUnknownPaddedChunks()
    {
        byte[] samples =
        [
            0x00, 0x80,
            0x00, 0x00,
            0xFF, 0x7F,
        ];
        byte[] wave = BuildPcm16Wave(samples, sampleRate: 8000, channels: 1, includeOddJunkChunk: true);

        Assert.True(EnbRetailWaveAudio.TryDecodePcm16(
            wave,
            out EnbRetailWavePcm16 pcm,
            out string? diagnostic), diagnostic);
        Assert.Equal(8000, pcm.SampleRate);
        Assert.Equal(1, pcm.Channels);
        Assert.Equal("PCM16", pcm.SourceFormat);
        Assert.Equal(3, pcm.FrameCount);
        Assert.Equal(samples, pcm.Pcm16LittleEndian);
        Assert.True(EnbCurrentRetailRsl3dSoundRuntime.TryValidateDecodedSpatialPcm(
            pcm,
            out string? spatialDiagnostic), spatialDiagnostic);
    }

    [Fact]
    public void PositionalSubmissionFailsClosedForStereoDecodedPcm()
    {
        byte[] stereoSamples =
        [
            0x00, 0x00, 0x00, 0x00,
            0xFF, 0x7F, 0x00, 0x80,
        ];
        byte[] wave = BuildPcm16Wave(stereoSamples, sampleRate: 8000, channels: 2, includeOddJunkChunk: false);

        Assert.True(EnbRetailWaveAudio.TryDecodePcm16(
            wave,
            out EnbRetailWavePcm16 pcm,
            out string? decodeDiagnostic), decodeDiagnostic);
        Assert.Equal(2, pcm.Channels);
        Assert.False(EnbCurrentRetailRsl3dSoundRuntime.TryValidateDecodedSpatialPcm(
            pcm,
            out string? spatialDiagnostic));
        Assert.Contains("mono only", spatialDiagnostic);
    }

    [Fact]
    public void ManagedWaveDecoderFailsClosedOnUnsupportedContainerData()
    {
        Assert.False(EnbRetailWaveAudio.TryDecodePcm16(
            [1, 2, 3, 4, 5, 6],
            out _,
            out string? diagnostic));
        Assert.Contains("RIFF/WAVE", diagnostic);
    }

    [Fact]
    public void ResolvedLooseSoundReaderConsumesTheAlreadySelectedRetailResource()
    {
        string path = Path.Combine(Path.GetTempPath(), $"enbnext-r6z26-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, [4, 3, 2, 1]);
            var choice = new EnbRetailSoundResolvedChoice(
                new EnbRetailSoundFileChoice("selected.wav", null, false),
                EnbRetailSoundResourceStatus.LooseFile,
                path,
                null,
                []);

            Assert.True(EnbRetailSoundResourceReader.TryRead(
                choice,
                retailMixResources: null,
                out byte[] bytes,
                out string source,
                out string? diagnostic), diagnostic);
            Assert.Equal([4, 3, 2, 1], bytes);
            Assert.Contains(path, source, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EnbRetailSoundDefinition ParseSingle(string text)
    {
        EnbRetailSoundCatalog catalog = EnbRetailSoundCatalog.ParseText(text);
        return Assert.Single(catalog.Definitions);
    }

    private static EnbRetailSoundResolution ExactLooseResolution(EnbRetailSoundDefinition definition)
    {
        EnbRetailSoundFileChoice authored = Assert.Single(definition.Files);
        return new EnbRetailSoundResolution(
            definition,
            [new EnbRetailSoundResolvedChoice(
                authored,
                EnbRetailSoundResourceStatus.LooseFile,
                Path.Combine(Path.GetTempPath(), authored.LogicalResourceName ?? "sound.wav"),
                null,
                [])]);
    }

    private static EnbRetailSoundResolvedChoice ExactChoice(string name, int? weight)
        => new(
            new EnbRetailSoundFileChoice(name, weight, false),
            EnbRetailSoundResourceStatus.LooseFile,
            Path.Combine(Path.GetTempPath(), name),
            null,
            []);

    private static byte[] BuildPcm16Wave(
        byte[] samples,
        int sampleRate,
        short channels,
        bool includeOddJunkChunk)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WAVE"u8);

        if (includeOddJunkChunk)
        {
            writer.Write("JUNK"u8);
            writer.Write(1u);
            writer.Write((byte)0x7A);
            writer.Write((byte)0x00);
        }

        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        ushort blockAlign = checked((ushort)(channels * 2));
        writer.Write((uint)(sampleRate * blockAlign));
        writer.Write(blockAlign);
        writer.Write((ushort)16);

        writer.Write("data"u8);
        writer.Write((uint)samples.Length);
        writer.Write(samples);
        if ((samples.Length & 1) != 0)
            writer.Write((byte)0);
        writer.Flush();

        byte[] result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)(result.Length - 8)));
        return result;
    }

    private static EnbRslStateFlags Flags(
        bool stationary = false,
        bool dontStopSound = false,
        bool dontLoopSound = false)
        => new(
            Delete: false,
            Deactivate: false,
            Stop: false,
            ActivateNext: false,
            Enabled: false,
            RemovePrototype: false,
            Precached: false,
            InstanceData: false,
            ActivateNextOnce: false,
            StopOnce: false,
            Stationary: stationary,
            StartsEnabled: false,
            DontStopSound: dontStopSound,
            DontLoopSound: dontLoopSound);
}
