using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbInventoryOwnershipTests
{
    private static Net7ItemTemplate Template()
    {
        var empty=new Net7ItemTemplateVector([],null);
        return new(123,10,101,16,new([new(21,Net7ItemTemplateAttributeType.Float,BitConverter.SingleToUInt32Bits(2.6f),null)],empty,empty),
            1,2,8,100,1,0,4,new(Encoding.Latin1.GetBytes("Test weapon")),new([]),new([]));
    }
    private static void Set(Net7AuxDataSchema schema,string path,object value)
    {var p=schema.Properties.Single(p=>p.Path==schema.Name+"."+path);p.Value=value is string s?new Net7AuxDataString(Encoding.Latin1.GetBytes(s)):value;p.State=2;}
    [Fact]
    public void ReusesUnchangedItemsButOldFramesStayDetached()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var owner=new EnbInventoryPublicationOwner();
        var catalog=new Net7ItemTemplateCatalog();catalog.Materialize(Template());
        Set(schemas.Ship,"Inventory.Equipped.4.ItemTemplateID",123);
        Set(schemas.Ship,"Inventory.Equipped.4.InstanceInfo","21:1.6");
        byte[] state=[0,32,0,0];Set(schemas.Ship,"Inventory.Equipped.4.ItemState",state);
        EnbSceneInventorySnapshot Sample()=>EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null,owner:owner);
        var first=Sample();var second=Sample();
        Assert.Equal(first.Equipped,second.Equipped);Assert.Same(first.Equipped[0],second.Equipped[0]);
        state[1]=0;var third=Sample();Assert.True(first.Equipped[0].AutoFire);Assert.False(third.Equipped[0].AutoFire);
        Assert.Equal(first.Equipped[0].Attributes,third.Equipped[0].Attributes);
        var wire=(Net7AuxDataString)schemas.Ship.Properties.Single(p=>p.Path==schemas.Ship.Name+".Inventory.Equipped.4.InstanceInfo").Value!;
        wire.Bytes[^1]=(byte)'8';var fourth=Sample();
        Assert.Equal("1.6",first.Equipped[0].Attributes.Single(a=>a.Id==21).Value);
        Assert.Equal("1.8",fourth.Equipped[0].Attributes.Single(a=>a.Id==21).Value);
        Set(schemas.Ship,"Inventory.Equipped.4.ItemTemplateID",-1);Assert.Empty(Sample().Equipped);Assert.Single(first.Equipped);
    }
    [Fact]
    public void MissingTemplateAndReplacementCollectionsRefresh()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var owner=new EnbInventoryPublicationOwner();var catalog=new Net7ItemTemplateCatalog();
        Set(schemas.Ship,"Inventory.Cargo.0.ItemTemplateID",123);
        var missing=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null,owner:owner);
        Assert.Equal("Null Item",missing.Cargo[0].Name);catalog.Materialize(Template());
        var ready=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null,owner:owner);
        Assert.Equal("Test weapon",ready.Cargo[0].Name);Assert.Equal("Null Item",missing.Cargo[0].Name);
        var replacement=new Net7AuxDataSchemaCatalog();
        Assert.Empty(EnbSceneInventorySnapshotBuilder.Build(replacement.Ship,replacement.LocalPlayer,catalog,null,owner:owner).Cargo);Assert.Single(ready.Cargo);
    }
    [Fact]
    public void WarmCollectionSamplingAllocatesNothing()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var owner=new EnbInventoryPublicationOwner();var catalog=new Net7ItemTemplateCatalog();catalog.Materialize(Template());
        Set(schemas.Ship,"Inventory.Cargo.0.ItemTemplateID",123);Set(schemas.Ship,"Inventory.Cargo.0.InstanceInfo","21:1.6");
        var collection=schemas.Ship.Properties.Single(p=>p.Path==schemas.Ship.Name+".Inventory.Cargo");
        var sample=typeof(EnbInventoryPublicationOwner).GetMethod("Sample",BindingFlags.Instance|BindingFlags.NonPublic)!
            .CreateDelegate<Func<Net7AuxDataProperty,byte,bool,Net7ItemTemplateCatalog,ImmutableArray<EnbSceneItemSnapshot>>>(owner);
        for(int i=0;i<100;i++)sample(collection,1,false,catalog);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)sample(collection,1,false,catalog);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
    }
    [Fact]
    public void RandomMutationsMatchFreshDetachedProjection()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var owner=new EnbInventoryPublicationOwner();var catalog=new Net7ItemTemplateCatalog();
        var random=new Random(301401);byte[] state=[0,0,0,0];
        Set(schemas.Ship,"Inventory.Equipped.2.ItemState",state);
        for(int step=0;step<300;step++)
        {
            if(step==80)catalog.Materialize(Template());
            switch(random.Next(7))
            {
                case 0:Set(schemas.Ship,"Inventory.Equipped.2.ItemTemplateID",random.Next(3)==0?-1:123);break;
                case 1:Set(schemas.Ship,"Inventory.Equipped.2.StackCount",random.Next(20));break;
                case 2:Set(schemas.Ship,"Inventory.Equipped.2.InstanceInfo",random.Next(2)==0?"21:1.6":"21:0");break;
                case 3:Set(schemas.Ship,"Inventory.Equipped.2.Structure",random.Next(2)==0?float.NaN:.5f);break;
                case 4:state[1]=(byte)random.Next(256);break;
                case 5:Set(schemas.Ship,"Inventory.Equipped.2.BuilderName",random.Next(2)==0?"":"builder");break;
                case 6:Set(schemas.Ship,"Inventory.Equipped.5.ItemTemplateID",random.Next(2)==0?-2:123);break;
            }
            var retained=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null,owner:owner);
            var fresh=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(fresh.Equipped),System.Text.Json.JsonSerializer.Serialize(retained.Equipped));
        }
    }
    [Fact]
    public void SignedZeroIsNotLostAcrossRetainedObservations()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var owner=new EnbInventoryPublicationOwner();
        Set(schemas.Ship,"Inventory.Equipped.0.ItemTemplateID",123);
        Set(schemas.Ship,"Inventory.Equipped.0.Structure",0f);
        var first=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,null,null,owner:owner);
        Set(schemas.Ship,"Inventory.Equipped.0.Structure",BitConverter.Int32BitsToSingle(int.MinValue));
        var next=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,null,null,owner:owner);
        Assert.Equal(0,BitConverter.SingleToInt32Bits(first.Equipped[0].Structure!.Value));
        Assert.Equal(int.MinValue,BitConverter.SingleToInt32Bits(next.Equipped[0].Structure!.Value));
    }
}
