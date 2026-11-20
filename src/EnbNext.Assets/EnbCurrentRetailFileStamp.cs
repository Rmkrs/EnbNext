using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EnbNext.Assets;

/// <summary>00684A60's packed DOS stamp, using the same Windows operations on
/// the actual file handle. No local-time conversion or managed rounding.</summary>
public static class EnbCurrentRetailFileStamp
{
    public static bool TryRead(string path,out uint stamp)
    {
        stamp=0;
        if(!OperatingSystem.IsWindows())return TryReadPortable(path,out stamp);
        try
        {
            using var handle=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(!GetFileInformationByHandle(handle,out var information))return false;
            // Retail ignores a failed DOS conversion and consumes incoming stack
            // bytes. Do not publish fabricated metadata for that undefined case.
            if(!FileTimeToDosDateTime(in information.LastWriteTime,out ushort date,out ushort time))return false;
            stamp=((uint)date<<16)|time;
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The same packed stamp from the file's UTC write time, as
    /// FileTimeToDosDateTime produces it from the handle's FILETIME.</summary>
    private static bool TryReadPortable(string path,out uint stamp)
    {
        stamp=0;
        try
        {
            DateTime time=File.GetLastWriteTimeUtc(EnbNext.Installation.EnbPathCase.Resolve(path));
            if(time.Year<1980||time.Year>2107)return false;
            uint date=(uint)(((time.Year-1980)<<9)|(time.Month<<5)|time.Day);
            uint clock=(uint)((time.Hour<<11)|(time.Minute<<5)|(time.Second/2));
            stamp=(date<<16)|clock;
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low,High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public FileTime CreationTime,LastAccessTime,LastWriteTime;
        public uint VolumeSerialNumber,SizeHigh,SizeLow,NumberOfLinks,IndexHigh,IndexLow;
    }

    [DllImport("kernel32.dll",SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation information);

    [DllImport("kernel32.dll",SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FileTimeToDosDateTime(in FileTime fileTime,out ushort date,out ushort time);
}
