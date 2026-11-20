using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z4InstalledPrototypeTokenAuditTests
{
    [Fact]
    public void CorpusAuditDistinguishesParticleEmitterDefinitionNameFromHlodReference()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] emitterHeader = new byte[0x14];
            Encoding.ASCII.GetBytes("PWEAPEXHAUST\0").CopyTo(emitterHeader.AsSpan(4));

            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("EMITTER.W3D", ContainerChunk(0x0500u, LeafChunk(0x0501u, emitterHeader))),
                ("PSS04.W3D", ContainerChunk(0x0700u, LeafChunk(0x0704u, Encoding.ASCII.GetBytes("pweapexhaust\0")))));

            EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX1 catalog was not loaded.");
            EnbInstalledW3dPrototypeTokenAuditReport report =
                EnbInstalledW3dPrototypeTokenAudit.Scan(catalog, "PWEAPEXHAUST");

            Assert.Equal(2, report.ResourcesContainingToken);
            Assert.Equal(1, report.ParticleEmitterNameHeaderMatchCount);

            EnbInstalledW3dPrototypeTokenMatch emitter = Assert.Single(
                report.Matches,
                static match => match.IsParticleEmitterNameHeaderMatch);
            Assert.Equal("EMITTER.W3D", emitter.ResourceName);
            Assert.Equal(0x0500u, emitter.TopLevelChunkType);
            Assert.Equal(0x0501u, emitter.DirectChildChunkType);
            Assert.Equal(emitter.DirectChildChunkPayloadOffset + 4, emitter.TokenOffset);
            Assert.Equal(0x14, emitter.DirectChildChunkPayloadLength);

            EnbInstalledW3dPrototypeTokenMatch hlod = Assert.Single(
                report.Matches,
                static match => match.ResourceName == "PSS04.W3D");
            Assert.Equal(0x0700u, hlod.TopLevelChunkType);
            Assert.False(hlod.IsParticleEmitterNameHeaderMatch);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    [Fact]
    public void CorpusAuditDoesNotPromoteTokenElsewhereInsideEmitterHeader()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] emitterHeader = new byte[0x14];
            Encoding.ASCII.GetBytes("PWEAPEXHAUST\0").CopyTo(emitterHeader.AsSpan(0));

            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("NOT-NAMED-EMITTER.W3D", ContainerChunk(0x0500u, LeafChunk(0x0501u, emitterHeader))));

            EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX1 catalog was not loaded.");
            EnbInstalledW3dPrototypeTokenAuditReport report =
                EnbInstalledW3dPrototypeTokenAudit.Scan(catalog, "PWEAPEXHAUST");

            EnbInstalledW3dPrototypeTokenMatch match = Assert.Single(report.Matches);
            Assert.Equal(0x0500u, match.TopLevelChunkType);
            Assert.Equal(0x0501u, match.DirectChildChunkType);
            Assert.False(match.IsParticleEmitterNameHeaderMatch);
            Assert.Equal(0, report.ParticleEmitterNameHeaderMatchCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorpusAuditUsesFirstRegisteredPackageAndReportsDirectNamedResource()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] emitterHeader = new byte[0x14];
            Encoding.ASCII.GetBytes("PWEAPEXHAUST\0").CopyTo(emitterHeader.AsSpan(4));

            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("SHADOW.W3D", LeafChunk(0x1234u, Encoding.ASCII.GetBytes("replacement\0"))),
                ("PWEAPEXHAUST.W3D", ContainerChunk(0x0500u, LeafChunk(0x0501u, emitterHeader))));
            // Retail 004E5440: the first registered package keeps SHADOW.W3D.
            WriteMix1(
                Path.Combine(directory, "mixfile_art_02.mix"),
                ("SHADOW.W3D", ContainerChunk(0x0500u, LeafChunk(0x0501u, emitterHeader))));

            EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX1 catalog was not loaded.");
            EnbInstalledW3dPrototypeTokenAuditReport report =
                EnbInstalledW3dPrototypeTokenAudit.Scan(catalog, "pweapexhaust");

            Assert.Equal("PWEAPEXHAUST.W3D", report.DirectNamedResourceName);
            Assert.Equal("mixfile_art_01.mix", report.DirectNamedResourceArchiveName);
            Assert.DoesNotContain(report.Matches, static match => match.ResourceName == "SHADOW.W3D");
            Assert.Equal(1, report.ParticleEmitterNameHeaderMatchCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteMix1(string path, params (string Name, byte[] Payload)[] resources)
    {
        const uint headerLength = 16;
        uint cursor = headerLength;
        var dataOffsets = new uint[resources.Length];
        for (int index = 0; index < resources.Length; index++)
        {
            dataOffsets[index] = cursor;
            cursor = checked(cursor + (uint)resources[index].Payload.Length);
        }

        uint directoryOffset = cursor;
        uint filenameTableOffset = checked(directoryOffset + 4u + (uint)(resources.Length * 12));

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x3158494Du);
        writer.Write(directoryOffset);
        writer.Write(filenameTableOffset);
        writer.Write(0u);
        foreach (var resource in resources)
            writer.Write(resource.Payload);

        writer.Write((uint)resources.Length);
        for (int index = 0; index < resources.Length; index++)
        {
            writer.Write(unchecked(0xB400_0000u + (uint)index));
            writer.Write(dataOffsets[index]);
            writer.Write((uint)resources[index].Payload.Length);
        }

        writer.Write((uint)resources.Length);
        foreach (var resource in resources)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(resource.Name + "\0");
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
        }
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, container: false);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static bytes => bytes).ToArray(), container: true);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4, 4),
            checked((uint)payload.Length) | (container ? 0x8000_0000u : 0u));
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "enbnext-r6z4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
