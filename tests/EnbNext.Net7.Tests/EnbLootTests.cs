using EnbNext.Engine;
using System.Text;
using System.Buffers.Binary;
using System.Text.Json;
using EnbNext.Protocol.Net;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbLootTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(2400)]
    public void PartialHulkReceiptPreservesOriginalEnvelopeWithoutApplyingItsTail(int trailingBytes)
    {
        var source=new Net7AuxDataSchemaCatalog().CreateObjectSchema(0x19)!;
        var first=source.Properties.Single(p=>p.Path=="HulkAuxData.Cargo.0.ItemTemplateID");
        var last=source.Resolve(693);
        // All bounded partial reads retain their prefix without an issue;
        // the captured final empty-slot (-2) form has separate replay coverage.
        first.Value=123;first.State=2;last.Value=-3;last.State=2;
        byte[] full=new Net7AuxDataCodec().Encode(source,[new(first.Id,Net7AuxDataOperationKind.ValueUpdate),new(last.Id,Net7AuxDataOperationKind.ValueUpdate)]);
        int declared=full.Length-2;
        byte[] payload=new byte[6+declared+trailingBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,99);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4),(ushort)declared);
        full.AsSpan(0,declared).CopyTo(payload.AsSpan(6));
        payload.AsSpan(6+declared).Fill(0xFF);
        var world=new Net7WorldState();
        var hulk=world.MaterializeCreate(new Net7CreateUpdate(99,1,1961,0x19,0,0,0));
        var result=world.Apply(Packet.ForOpcode(0x001B,payload),1234);
        Assert.Null(result.AuxDataWarning);Assert.Null(result.AuxDataCompatibilityNote);
        Assert.Equal(123,hulk.AuxData!.Resolve(first.Id).Value);
        Assert.Null(hulk.AuxData.Resolve(693).Value);
        using var receipt=JsonDocument.Parse(result.AuxDataFailureReceipt!);
        var root=receipt.RootElement;
        Assert.Equal(1234,root.GetProperty("ClientTime").GetInt32());
        Assert.Equal(1961,root.GetProperty("BaseAsset").GetInt32());
        Assert.Equal(declared,root.GetProperty("DeclaredDeltaBytes").GetInt32());
        Assert.Equal(trailingBytes,root.GetProperty("TrailingBytes").GetInt32());
        Assert.Equal(payload.Length<=2048,root.GetProperty("Complete").GetBoolean());
        Assert.Equal(payload.AsSpan(0,Math.Min(payload.Length,2048)).ToArray(),Convert.FromHexString(root.GetProperty("PayloadHex").GetString()!));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),root.GetProperty("PayloadSha256").GetString());
        var issues=new EnbNext.Engine.LiveIssueLog();
        if (result.AuxDataWarning is not null)
            issues.ObserveDiagnostic("WORLD-09 partial materialization: "+result.AuxDataWarning);
        Assert.Equal(0,issues.Count);
        // A later complete ordinary packet recovers the final field and emits no failure receipt.
        byte[] complete=new byte[6+full.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(complete,99);
        BinaryPrimitives.WriteUInt16LittleEndian(complete.AsSpan(4),(ushort)full.Length);
        full.CopyTo(complete,6);
        var recovered=world.Apply(Packet.ForOpcode(0x001B,complete),1300);
        Assert.Null(recovered.AuxDataWarning);Assert.Null(recovered.AuxDataFailureReceipt);
        Assert.Equal(-3,hulk.AuxData.Resolve(693).Value);
    }
    [Fact]
    public void TruncatedFinalCorpseSlotRetainsCompletedInventoryAndCanRecover()
    {
        var catalog=new Net7AuxDataSchemaCatalog();
        var source=catalog.CreateObjectSchema(0x19)!;
        var target=catalog.CreateObjectSchema(0x19)!;
        var operations=new List<Net7AuxDataOperation>();
        void Set(string path,object value)
        {
            var p=source.Properties.Single(p=>p.Path=="HulkAuxData."+path);
            p.Value=value;p.State=2;operations.Add(new(p.Id,Net7AuxDataOperationKind.ValueUpdate));
        }
        Set("Name",new Net7AuxDataString(Encoding.Latin1.GetBytes("Corpse of Dionysian Spore Sac")));
        Set("Cargo.0.ItemTemplateID",123);Set("Cargo.0.StackCount",2);
        for(int i=1;i<40;i++)Set($"Cargo.{i}.ItemTemplateID",-2);
        var codec=new Net7AuxDataCodec();
        byte[] full=codec.Encode(source,operations);
        Assert.NotEqual(0,full[0]);
        string? diagnostic=codec.ApplyObjectDelta(target,full.AsSpan(0,full.Length-2),default);
        Assert.Contains("Retained completed",diagnostic);
        Assert.Equal(123,target.Properties.Single(p=>p.Path=="HulkAuxData.Cargo.0.ItemTemplateID").Value);
        Assert.Null(target.Resolve(693).Value);
        codec.ApplyObjectDelta(target,full,default);
        Assert.Equal(-2,target.Resolve(693).Value);

        // Strict/offline decoding retains its transactional contract.
        var strict=catalog.CreateObjectSchema(0x19)!;
        Assert.Throws<InvalidDataException>(()=>codec.Apply(strict,full.AsSpan(0,full.Length-2)));
        Assert.Null(strict.Resolve(1).Value);
    }

    [Fact]
    public void LootProjectionRetainsCollectionSlotAndCorpseOwnership()
    {
        var world=new Net7WorldState();
        var hulk=world.MaterializeCreate(new Net7CreateUpdate(99,1,1,0x19,0,0,0));
        void Set(string path,object value)
        {var p=hulk.AuxData!.Properties.Single(p=>p.Path=="HulkAuxData."+path);p.Value=value;p.State=2;}
        Set("Cargo.3.ItemTemplateID",123);Set("Cargo.3.StackCount",2);
        Set("Cargo.0.ItemTemplateID",-1);Set("Cargo.1.ItemTemplateID",-2);
        Set("Equipped.0.ItemTemplateID",456);Set("Equipped.0.StackCount",1);
        var snapshot=EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,null,null,hulk);
        Assert.Equal(99u,snapshot.HulkId);Assert.Equal(hulk.CreationOrdinal,snapshot.HulkOwner);
        Assert.Equal(123,snapshot.Find(6,3)!.TemplateId);Assert.Equal(456,snapshot.Find(5,0)!.TemplateId);
        Assert.Equal(new[]{0,2,3},snapshot.LootSlotKeys.Take(3));
        Set("Cargo.0.ItemTemplateID",-2);
        Assert.Equal(0,snapshot.LootSlotKeys[0]); // detached snapshot
        Assert.Equal(2,EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,null,null,hulk).LootSlotKeys[0]);
        Set("Cargo.3.StackCount",0);
        Assert.Equal(2,snapshot.Find(6,3)!.Count);
        Assert.Equal(0,EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,null,null,hulk).Find(6,3)!.Count);
        Set("Cargo.3.ItemTemplateID",-1);
        Assert.Null(EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,null,null,hulk).Find(6,3));
        Assert.Empty(EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,null,null).Loot);
    }
}
