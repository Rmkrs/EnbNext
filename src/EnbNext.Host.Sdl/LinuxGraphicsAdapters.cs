using System.Runtime.InteropServices;

namespace EnbNext.Host.Sdl;

/// <summary>
/// Linux: the graphics cards the kernel's DRM lists. Which driver renders is settled when the process loads EGL, so a
/// chosen card takes effect at the next start: <see cref="Apply"/> sets the variables Mesa (DRI_PRIME) and NVIDIA
/// (PRIME render offload) read, before SDL loads EGL. Variables the player already set win.
/// </summary>
internal static class LinuxGraphicsAdapters
{
    internal readonly record struct Card(GraphicsAdapter Adapter,string Slot,string Driver,bool Primary);

    internal static Card[] List(string root="/sys/class/drm",string[]? pciIds=null)
    {
        var found=new List<(string Slot,uint Vendor,uint Device,string Driver,bool Primary)>();
        try
        {
            if(!Directory.Exists(root))return [];
            foreach(var dir in Directory.GetDirectories(root,"card*"))
            {
                string name=Path.GetFileName(dir);
                if(name.Length<5||name.AsSpan(4).ContainsAnyExcept("0123456789"))continue; // card0, not card0-DP-1
                string device=Path.Combine(dir,"device");
                // PCI cards only: a PCI slot such as 0000:01:00.0 (no simpledrm or virtual displays).
                if(Target(device) is not {} slot||!slot.Contains(':')||found.Exists(f=>f.Slot==slot))continue;
                if(!Hex(Path.Combine(device,"vendor"),out uint vendor)||!Hex(Path.Combine(device,"device"),out uint id))continue;
                string bootVga=Path.Combine(device,"boot_vga");
                bool primary=File.Exists(bootVga)&&File.ReadAllText(bootVga).Trim()=="1";
                found.Add((slot,vendor,id,Target(Path.Combine(device,"driver"))??"",primary));
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return [];}
        found.Sort((a,b)=>string.CompareOrdinal(a.Slot,b.Slot));
        var names=Names(pciIds??["/usr/share/hwdata/pci.ids","/usr/share/misc/pci.ids","/usr/share/pci.ids"]);
        var result=new Card[found.Count];
        for(int i=0;i<found.Count;i++)
        {
            var f=found[i];int same=0;
            for(int j=0;j<i;j++)if(found[j].Vendor==f.Vendor&&found[j].Device==f.Device)same++;
            result[i]=new(new($"{f.Vendor:X4}-{f.Device:X4}-{same}",Name(f.Vendor,f.Device,names),f.Vendor,f.Device,0,0),f.Slot,f.Driver,f.Primary);
        }
        return result;
    }

    /// <summary>Before SDL loads EGL: selects the saved card's driver. Returns the key applied, or null (Automatic,
    /// or the card is gone).</summary>
    internal static string? Apply(string? key,string root="/sys/class/drm",string[]? pciIds=null)
    {
        if(!OperatingSystem.IsLinux()||string.IsNullOrEmpty(key))return null;
        Card? chosen=null;
        foreach(var card in List(root,pciIds))if(card.Adapter.Key==key)chosen=card;
        if(chosen is not {} c)return null;
        if(c.Driver=="nvidia")
        {
            // NVIDIA PRIME render offload; for EGL this variable is all that is needed. The primary card needs none.
            if(!c.Primary)Set("__NV_PRIME_RENDER_OFFLOAD","1");
        }
        else
        {
            Set("DRI_PRIME","pci-"+c.Slot.Replace(':','_').Replace('.','_'));
            // glvnd asks NVIDIA's EGL first when it is installed; only Mesa's then, or DRI_PRIME is never read.
            foreach(var dir in (string[])["/usr/share/glvnd/egl_vendor.d","/etc/glvnd/egl_vendor.d"])
            {
                string mesa=Path.Combine(dir,"50_mesa.json");
                if(File.Exists(mesa)){Set("__EGL_VENDOR_LIBRARY_FILENAMES",mesa);break;}
            }
        }
        return c.Adapter.Key;
    }

    [DllImport("libc",EntryPoint="setenv",CharSet=CharSet.Ansi)] private static extern int SetEnv(string name,string value,int overwrite);
    // The native environment: .NET's own copy does not reach libEGL, Mesa or the browser's processes.
    private static void Set(string name,string value){if(Environment.GetEnvironmentVariable(name) is null)SetEnv(name,value,0);}

    private static string? Target(string path)
    {
        var link=new DirectoryInfo(path).LinkTarget;
        return link is null?null:Path.GetFileName(link.TrimEnd('/'));
    }
    private static bool Hex(string path,out uint value)
    {
        value=0;if(!File.Exists(path))return false;
        string text=File.ReadAllText(path).Trim();if(text.StartsWith("0x",StringComparison.OrdinalIgnoreCase))text=text[2..];
        return uint.TryParse(text,System.Globalization.NumberStyles.HexNumber,null,out value);
    }

    private static string Name(uint vendor,uint device,Dictionary<uint,(string Vendor,Dictionary<uint,string> Devices)> names)
    {
        string brand=vendor switch {0x10DE=>"NVIDIA",0x1002=>"AMD",0x8086=>"Intel",_=>names.TryGetValue(vendor,out var v)?v.Vendor:$"{vendor:X4}"};
        if(names.TryGetValue(vendor,out var known)&&known.Devices.TryGetValue(device,out var model))
        {
            // pci.ids: "AD103 [GeForce RTX 4080]"; the bracketed part is the marketing name.
            int open=model.LastIndexOf('['),close=model.LastIndexOf(']');
            if(open>=0&&close>open+1)model=model[(open+1)..close];
            return brand+" "+model;
        }
        return $"{brand} graphics ({vendor:X4}:{device:X4})";
    }
    private static Dictionary<uint,(string Vendor,Dictionary<uint,string> Devices)> Names(string[] paths)
    {
        var result=new Dictionary<uint,(string,Dictionary<uint,string>)>();
        foreach(var path in paths)
        {
            try
            {
                if(!File.Exists(path))continue;
                Dictionary<uint,string>? devices=null;
                foreach(var line in File.ReadLines(path))
                {
                    if(line.Length<6||line[0]=='#')continue;
                    if(line.StartsWith("C ",StringComparison.Ordinal))break; // device classes follow the vendors
                    if(line[0]!='\t')
                    {
                        devices=null;
                        if(uint.TryParse(line.AsSpan(0,4),System.Globalization.NumberStyles.HexNumber,null,out uint id))
                        {devices=[];result[id]=(line[4..].Trim(),devices);}
                    }
                    else if(line.Length>6&&line[1]!='\t'&&devices is not null&&uint.TryParse(line.AsSpan(1,4),System.Globalization.NumberStyles.HexNumber,null,out uint model))
                        devices[model]=line[5..].Trim();
                }
                return result;
            }
            catch(IOException){result.Clear();}
        }
        return result;
    }
}
