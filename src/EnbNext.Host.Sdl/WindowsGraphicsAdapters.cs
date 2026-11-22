using System.Runtime.InteropServices;

namespace EnbNext.Host.Sdl;

/// <summary>A graphics card as Windows lists it. <see cref="Key"/> is what the settings keep: the card's vendor and
/// device ids and its place among identical cards. The LUID is only valid until the next restart of Windows.</summary>
internal readonly record struct GraphicsAdapter(string Key,string Name,uint Vendor,uint Device,ulong VideoMemory,long Luid);

/// <summary>The hardware graphics cards DXGI reports, in its order (the first is the one Windows picks for this program).</summary>
internal static unsafe class WindowsGraphicsAdapters
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId,DeviceId,SubSysId,Revision;
        public nuint DedicatedVideoMemory,DedicatedSystemMemory,SharedSystemMemory;
        public uint LuidLow;public int LuidHigh;public uint Flags;
    }
    private const uint SoftwareAdapter=2;
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid riid,out nint factory);
    private static readonly Guid Factory1=new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>The cards, or none where DXGI is unavailable. Software renderers are left out.</summary>
    internal static GraphicsAdapter[] List()
    {
        if(!OperatingSystem.IsWindows())return [];
        nint factory=0;
        try
        {
            if(CreateDXGIFactory1(Factory1,out factory)<0||factory==0)return [];
            var table=*(nint**)factory;
            var enumerate=(delegate* unmanaged<nint,uint,nint*,int>)table[12];
            var result=new List<GraphicsAdapter>();
            for(uint i=0;;i++)
            {
                nint adapter;if(enumerate(factory,i,&adapter)<0||adapter==0)break;
                try
                {
                    var describe=(delegate* unmanaged<nint,AdapterDescription*,int>)(*(nint**)adapter)[10];
                    AdapterDescription d;if(describe(adapter,&d)<0||(d.Flags&SoftwareAdapter)!=0)continue;
                    string name=new string(d.Description).TrimEnd('\0').Trim();
                    uint vendor=d.VendorId,device=d.DeviceId;int same=0;
                    foreach(var listed in result)if(listed.Vendor==vendor&&listed.Device==device)same++;
                    result.Add(new($"{vendor:X4}-{device:X4}-{same}",name.Length==0?"Graphics card "+(result.Count+1):name,
                        vendor,device,d.DedicatedVideoMemory,((long)d.LuidHigh<<32)|d.LuidLow));
                }
                finally{Release(adapter);}
            }
            return [..result];
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or SEHException){return [];}
        finally{if(factory!=0)Release(factory);}
    }
    /// <summary>The card a saved key names, if it is present now.</summary>
    internal static GraphicsAdapter? Find(string? key)
    {
        if(string.IsNullOrEmpty(key))return null;
        foreach(var adapter in List())if(adapter.Key==key)return adapter;
        return null;
    }
    internal static void Release(nint unknown)=>((delegate* unmanaged<nint,uint>)(*(nint**)unknown)[2])(unknown);
}
