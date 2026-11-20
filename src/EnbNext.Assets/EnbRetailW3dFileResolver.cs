namespace EnbNext.Assets;

/// <summary>0091D220 obtains W3D bytes from the same RunClient file provider
///as INI documents. Preserve a loose file's actual byte-range identity rather
///than substituting the installed package with a matching logical name.</summary>
public sealed class EnbRetailW3dFileResolver(EnbRetailFileResolver files, EnbMixResourceCatalog? textures)
{
    /// <summary>0091D4B0 derives the prefix before the first dot and retries
    /// the parent directory only when the primary FILE could not be opened.</summary>
    public bool TryResolveLazyFile(string name,out EnbResolvedW3dResource source,out string? diagnostic)
    {
        int dot=name.IndexOf('.');string file=(dot<0?name:name[..dot])+".w3d";
        if(TryResolve(file,name,out source,out diagnostic,out bool opened))return true;
        return !opened && TryResolve("..\\"+file,name,out source,out diagnostic);
    }

    /// <summary>009A3B00's aggregate-only full-name working-directory retry.</summary>
    public bool TryResolveAggregateFile(string name,out EnbResolvedW3dResource source,out string? diagnostic)
    {
        source=default!;diagnostic=null;
        if(!files.TryReadAggregateWorkingDirectoryFile(name,out var bytes,out var package,out var loose))return false;
        return TryDecodeFile(loose!,name,bytes,package,loose,textures,out source,out diagnostic);
    }
    public bool TryResolve(string fileName, string objectName,
        out EnbResolvedW3dResource source, out string? diagnostic)
        => TryResolve(fileName, objectName, out source, out diagnostic, out _);

    public bool TryResolve(string fileName, string objectName,
        out EnbResolvedW3dResource source, out string? diagnostic, out bool nativeLoadSucceeded)
    {
        source = default!;
        diagnostic = null;
        nativeLoadSucceeded = false;
        if (!files.TryRead(fileName, out var bytes, out var package, out var loose))
        {
            diagnostic = $"W3D file '{fileName}' is unavailable through the retail file provider.";
            return false;
        }
        //0091D29F calls File+1C: verified loose target006841D0 opens for
        //read and tests the handle, not the file length.0091D35E returns
        //success after the chunk walk, including an empty opened file.
        nativeLoadSucceeded = true;
        return TryDecodeFile(fileName,objectName,bytes,package,loose,textures,out source,out diagnostic);
    }

    public static bool TryDecodeFile(string fileName,string objectName,byte[] bytes,EnbMixResourceLocation? package,string? loose,
        EnbMixResourceCatalog? textures,
        out EnbResolvedW3dResource source,out string? diagnostic)
    {
        source=default!;
        if (!EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes, out var publication,out diagnostic))
        {
            diagnostic = $"W3D file '{fileName}' is outside the current decoded model families.";
            return false;
        }
        //EnbMixResourceLocation's reader consumes an exact physical byte range;
        //offset zero and the read length also identify an ordinary loose file.
        var location = package ?? new EnbMixResourceLocation(fileName, loose!, 0, bytes.Length);
        var model=publication.Model;
        source = new(fileName, objectName, location, model,
            EnbW3dResourceDependencies.ResolveTextureDependencies(model, textures))
            { LooseFilePath = loose,PrototypeFile=publication };
        return true;
    }
}
