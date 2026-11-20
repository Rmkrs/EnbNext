using System.Reflection;
using EnbNext.Net7.World;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbInventoryChildLookupTests
{
    [Fact]
    public void AllSchemaChildrenMatchOriginalLookupWithoutPerLookupAllocation()
    {
        var schemas=new Net7AuxDataSchemaCatalog();
        var lookup=typeof(EnbSceneInventorySnapshotBuilder).GetMethod("Child",BindingFlags.Static|BindingFlags.NonPublic)!
            .CreateDelegate<Func<Net7AuxDataProperty?,string,Net7AuxDataProperty?>>();
        static Net7AuxDataProperty? Original(Net7AuxDataProperty? p,string name)
            =>p?.Children.FirstOrDefault(c=>c.Path[(c.Path.LastIndexOf('.')+1)..]==name);
        var requests=schemas.Ship.Properties.Concat(schemas.LocalPlayer.Properties)
            .Select(p=>(Parent:p.Parent,Name:p.Path[(p.Path.LastIndexOf('.')+1)..])).ToArray();
        foreach(var request in requests)
            Assert.Same(Original(request.Parent,request.Name),lookup(request.Parent,request.Name));
        Assert.Null(lookup(null,"Inventory"));Assert.Null(lookup(schemas.Ship.Root,"absent"));
        Assert.Null(lookup(schemas.Ship.Root,"inventory"));Assert.Null(lookup(schemas.Ship.Root,""));
        long Measure(Func<Net7AuxDataProperty?,string,Net7AuxDataProperty?> method)
        {
            foreach(var r in requests)method(r.Parent,r.Name);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<4;i++)foreach(var r in requests)method(r.Parent,r.Name);
            return GC.GetAllocatedBytesForCurrentThread()-before;
        }
        long oldBytes=Measure(Original),newBytes=Measure(lookup);
        Assert.True(oldBytes>100000);Assert.Equal(0,newBytes);
        string? receipt=Environment.GetEnvironmentVariable("ENB_ALLOCATION_CPU_RECEIPT");
        if(receipt is not null)File.WriteAllText(receipt,System.Text.Json.JsonSerializer.Serialize(new
            {Lookups=requests.Length*4,OriginalAllocatedBytes=oldBytes,CurrentAllocatedBytes=newBytes,Exact=true}));
    }
}
