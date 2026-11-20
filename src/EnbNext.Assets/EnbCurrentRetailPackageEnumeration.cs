using System.Runtime.InteropServices;

namespace EnbNext.Assets;

/// <summary>004E4D10/00A28A53/00A28B20 package registration order. The
/// Windows result sequence is retained without sorting the package names.</summary>
public static class EnbCurrentRetailPackageEnumeration
{
    public static bool TryRead(string directory,out IReadOnlyList<string> names)
    {
        names=[];
        if(!OperatingSystem.IsWindows())
        {
            string resolved=EnbNext.Installation.EnbPathCase.Resolve(directory);
            if(!Directory.Exists(resolved))return false;
            var found=EnbNext.Installation.EnbPathCase.EnumerateFiles(resolved,"*.MIX")
                .Select(Path.GetFileName).OfType<string>().ToList();
            names=found.AsReadOnly();
            return true;
        }
        nint handle=FindFirstFile(Path.Combine(directory,"*.MIX"),out var data);
        if(handle==-1)return Marshal.GetLastWin32Error()==2;
        var result=new List<string>();
        try
        {
            do{result.Add(data.Name);}while(FindNextFile(handle,out data));
            if(Marshal.GetLastWin32Error()!=18)return false;
            names=result.AsReadOnly();
            return true;
        }
        finally{FindClose(handle);}
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi,Pack=4)]
    private struct FindData
    {
        public uint Attributes;
        public long CreationTime,LastAccessTime,LastWriteTime;
        public uint SizeHigh,SizeLow,Reserved0,Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)]public string AlternateName;
    }

    [DllImport("kernel32.dll",EntryPoint="FindFirstFileA",CharSet=CharSet.Ansi,ExactSpelling=true,SetLastError=true)]
    private static extern nint FindFirstFile(string pattern,out FindData data);
    [DllImport("kernel32.dll",EntryPoint="FindNextFileA",CharSet=CharSet.Ansi,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFile(nint handle,out FindData data);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint handle);
}
