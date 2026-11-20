using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbVendorListTests
{
    [Fact]
    public void StackTextSuppressesNonStackableAndMissingLimits()
    {
        foreach(uint? count in new uint?[]{null,0,1,uint.MaxValue})Assert.Equal("",EnbVendorList.StackText(count));
        Assert.Equal("2 per stack",EnbVendorList.StackText(2));
    }

    [Fact]
    public void OwnedTextCountsMatchingSlotsEvenWhenTheirQuantitiesAreZero()
    {
        var item=Item(0,14) with {Count=0};
        var data=Data() with {Cargo=[item]};
        Assert.Equal("",data.OwnedText(item.TemplateId));
        Assert.Equal("Owned: 0 in 2 slots",(data with {Ammo=[item]}).OwnedText(item.TemplateId));
    }
    private static EnbSceneItemSnapshot Item(int slot,uint type,string name="Item",ulong price=0)
        => new(4,slot,100+slot,name,80,200,type,1,0,1,null,null,null,"","",[],[],0){Price=price};
    private static EnbSceneInventorySnapshot Data(params EnbSceneItemSnapshot[] stock)
        => new(null,30,[],[],[],ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty,[],[],[]){Vendor=[..stock]};

    [Fact]
    public void CategoriesUseItemTypeAscendingAndLimitToSixWithoutInventingGoods()
    {
        var stock=Enumerable.Range(0,19).Reverse().Select(i=>Item(i,(uint)i)).ToArray();
        Assert.Equal(new[]{0,1,2,3,4,5},EnbVendorList.Categories(EnbVendorList.CategoryMask(stock)));
        Assert.Equal(new[]{2,14},EnbVendorList.Categories(EnbVendorList.CategoryMask([Item(0,14),Item(1,2),Item(2,14),Item(3,uint.MaxValue)])));
        Assert.Empty(EnbVendorList.Categories(0));
        Assert.Equal("Shields",EnbVendorList.CategoryNames[2]);
        Assert.Equal("Beam",EnbVendorList.CategoryNames[14]);
    }

    [Fact]
    public void FilterRetainsServerSlotsAndNativeNullDefinitionType()
    {
        var data=Data(Item(12,14),Item(8,2),Item(25,uint.MaxValue));
        Assert.Equal(new[]{12,25},EnbVendorList.Rows(data,14,false,true).Select(i=>i.Slot));
        Assert.Equal(new[]{25},EnbVendorList.Rows(data,null,false,true).Select(i=>i.Slot));
    }

    [Fact]
    public void CompareUsesTemplateIdentityFromAllOwnedLayersEvenAtZeroCount()
    {
        var data=Data(Enumerable.Range(0,6).Select(i=>Item(i,(uint)i)).ToArray());
        data=data with {Cargo=[data.Vendor[0] with{Count=0}],Equipped=[data.Vendor[1]],Ammo=[data.Vendor[2]],Vault=[data.Vendor[3]]};
        Assert.Equal(new[]{0,1,2,3},EnbVendorList.Rows(data,14,true,true).Select(i=>i.Slot));
        Assert.Empty(EnbVendorList.Rows(data with{Cargo=[],Equipped=[],Ammo=[],Vault=[]},null,true,true));
    }

    [Fact]
    public void StableSortUsesCaseSensitiveNamesAndSigned64BitPrices()
    {
        var data=Data(Item(12,14,"a",10),Item(4,14,"Z",10),Item(7,14,"a",ulong.MaxValue),Item(9,14,"A",0));
        Assert.Equal(new[]{9,4,12,7},EnbVendorList.Rows(data,14,false,true).Select(i=>i.Slot));
        Assert.Equal(new[]{7,9,12,4},EnbVendorList.Rows(data,14,false,false).Select(i=>i.Slot));
    }

    [Fact]
    public void SortPreferenceDefaultsToPriceAndReadsTheNamedBoolean()
    {
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText("").TradeSortByName);
        string text=EnbRetailCinematicOptionsWriter.UpdateValues("",[("BOOL","Trade_Sort_By_Name","yes")]);
        Assert.True(EnbRetailPlayerOptionsCatalog.ParseText(text).TradeSortByName);
        text=EnbRetailCinematicOptionsWriter.UpdateValues(text,[("BOOL","Trade_Sort_By_Name","no")]);
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText(text).TradeSortByName);
    }
}
