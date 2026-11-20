using System.Text;

namespace EnbNext.Assets;

/// <summary>004E5440 registers the first package for each folded filename;
/// 004E57A0 prefers loose files, then invokes that package's checksum lookup.</summary>
public sealed class EnbCurrentRetailThumbnailFileSource
{
    private readonly EnbRetailFileResolver _loose;
    private readonly Dictionary<string,(string Path,EnbCurrentRetailMixSource Source,string Name)> _packages=new(StringComparer.Ordinal);

    private EnbCurrentRetailThumbnailFileSource(string workingDirectory)
        =>_loose=new(workingDirectory,null);

    public static bool TryLoad(string workingDirectory,string packageDirectory,out EnbCurrentRetailThumbnailFileSource? source)
    {
        source=null;
        if(!EnbCurrentRetailPackageEnumeration.TryRead(packageDirectory,out var names))return false;
        var loaded=new EnbCurrentRetailThumbnailFileSource(workingDirectory);
        foreach(string name in names)
        {
            string path=Path.Combine(packageDirectory,name);
            if(!EnbCurrentRetailMixSource.TryReadFile(path,out var package))continue;
            foreach(string resourceName in package!.Names)
                loaded._packages.TryAdd(EnbCurrentRetailTextureThumbnailTable.FoldAscii(resourceName),(path,package,resourceName));
        }
        source=loaded;
        return true;
    }

    public EnbCurrentRetailThumbnailSource? Read(string name)
    {
        int terminator=name.IndexOf('\0');
        if(terminator>=0)name=name[..terminator];
        if(name.Any(character=>character>255))return null;
        if(_loose.TryRead(name,out var looseBytes,out _,out string? loosePath))
            return EnbCurrentRetailFileStamp.TryRead(loosePath!,out uint looseStamp)?new(looseBytes,looseStamp)
                {ResourceLocation=new(name,loosePath!,0,looseBytes.Length)}:null;
        if(!_packages.TryGetValue(EnbCurrentRetailTextureThumbnailTable.FoldAscii(name),out var package))return null;
        uint hash=EnbCurrentRetailMixSource.ComputeNameHash(Encoding.Latin1.GetBytes(name),EnbCurrentRetailMixSource.ConvertStartupHashCharacter);
        if(!package.Source.TryReadResource(hash,out var bytes,out uint offset,out uint length) || !EnbCurrentRetailFileStamp.TryRead(package.Path,out uint stamp))return null;
        return new(bytes,stamp){ResourceLocation=new(package.Name,package.Path,offset,length)};
    }
}
