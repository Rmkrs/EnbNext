namespace EnbNext.Assets;

/// <summary>00469BF4..00469CC6, after a present PGM height source is selected.
/// Inputs are resolved-file +3C packed DOS stamps; null means that the file's
/// +14 existence query failed. This does not substitute UTC file times.</summary>
public static class EnbRetailTerrainPrelightCachePolicy
{
    /// <summary>469B26..469BB9 and default-C A27F25: fold ASCII, find
    /// the first dot, test the following three bytes, then change gm to ld.</summary>
    public static bool TryGetName(string heightName,out string cacheName)
    {
        int nul=heightName.IndexOf('\0');
        char[] name=(nul<0?heightName:heightName[..nul]).ToCharArray();
        for(int index=0;index<name.Length;index++)if(name[index] is >= 'A' and <= 'Z')name[index]=(char)(name[index]+32);
        int dot=Array.IndexOf(name,'.');cacheName="";
        if(dot<0||dot+3>=name.Length||name[dot+1]!='p'||name[dot+2]!='g'||name[dot+3]!='m')return false;
        name[dot+2]='l';name[dot+3]='d';cacheName=new(name);return true;
    }

    public static bool ShouldRead(uint heightStamp,uint? cacheStamp,uint? colorStamp,uint? iniStamp)
        =>cacheStamp is uint cache && cache>heightStamp &&
            (colorStamp is not uint color || color<cache) &&
            (iniStamp is not uint ini || ini<cache);
}
