using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbOrdinaryConfiguredSelectionTests
{
    [Theory]
    [InlineData("registered-variant",false)]
    [InlineData("registered-variant",true)]
    [InlineData("registered-original",false)]
    [InlineData("registered-original",true)]
    [InlineData("file-variant",false)]
    [InlineData("file-variant",true)]
    [InlineData("file-original",false)]
    [InlineData("file-original",true)]
    [InlineData("missing",false)]
    [InlineData("missing",true)]
    public void ConfiguredLookupChecksVariantThenOriginalOnBothSidesOfFileLoad(string scenario,bool effect)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=new EnbW3dRenderResourceCache();
        string name="RZX",variant=EnbCurrentRetailPrototypeVariantName.Create(name,2,Vector3.Zero);
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-Configured-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file=Path.Combine(directory,"authored.w3d");
        byte[] Make(string selected)=>EnbR6Z11CurrentRetailAggregateTests.BuildAggregate(selected,"NULL",[],25,0);
        void Publish(string selected)
        {
            byte[] bytes=Make(selected);
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var decoded,out var error),error);
            var source=new EnbResolvedW3dResource("registered-configured.w3d",selected,
                new("registered-configured.w3d","fixture",0,bytes.Length),decoded.Model,[]){PrototypeFile=decoded};
            Assert.True(cache.TryLoadFile(source,out _,out error),error);
        }
        try
        {
            if(scenario.StartsWith("registered"))Publish(scenario.EndsWith("variant")?variant:name);
            if(scenario.StartsWith("file"))File.WriteAllBytes(file,Make(scenario.EndsWith("variant")?variant:name));
            //A registered result must avoid even opening the malformed FILE.
            if(scenario.StartsWith("registered"))File.WriteAllBytes(file,[1,2,3,4]);
            assets.PresentationCatalog!.LoadBaseAssetsText($"[BASE-31000]\nFILE={file}\nNAME={name}\n");
            var context=cache.CreatePublicationContext();
            var names=cache.CapturePrototypeSources(out _);
            var actual=effect ? EnbCurrentRetailRObjEffectAsset.PrepareOwnedFactory(assets,31000,Vector3.Zero,
                registeredPrototypes:names,publicationContext:context,factoryInputs:(2,Vector3.Zero)) :
                EnbCurrentRetailRObjEffectAsset.SelectOrdinaryBaseFactory(assets,31000,context,names,2,Vector3.Zero);
            Assert.True(actual.PublicationsApplied);
            Assert.Equal(scenario=="missing",actual.IsMissingFactoryObject);
            Assert.Equal(scenario.EndsWith("variant"),actual.IsPreconfiguredFactoryObject);
            Assert.Equal(scenario=="missing"?"missing":scenario.EndsWith("variant")?variant:name,
                actual.Source!.RequestedObjectName,ignoreCase:true);
            if(scenario.StartsWith("registered"))Assert.Equal(0,context.PublicationCount);
            if(scenario.StartsWith("file"))Assert.True(context.PublicationCount>0);
            if(effect)
            {
                var built=actual.Build();
                Assert.Equal(scenario.EndsWith("variant"),built.IsPreconfiguredFactoryObject);
                Assert.Equal(scenario=="missing"||scenario.EndsWith("variant")?1f:2f,
                    built.ResolveFactoryDefinition(new(31000,false,AssetScale:2)).AssetScale);
            }
        }
        finally{Directory.Delete(directory,true);}
    }
}
