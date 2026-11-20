using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EnbNext.App;

internal sealed class AccountSecretStore
{
    public static string GameKey(string server,string login)=>"game/"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.Trim().ToLowerInvariant()+"\n"+login.Trim().ToLowerInvariant())));
    public Task<string?> ReadAsync(string key,CancellationToken token=default)=>OperatingSystem.IsWindows()?Task.Run(()=>ReadWindows(key),token):LinuxAsync("lookup",key,null,token);
    public async Task WriteAsync(string key,string value,CancellationToken token=default)
    {if(OperatingSystem.IsWindows())await Task.Run(()=>WriteWindows(key,value),token);else await LinuxAsync("store",key,value,token);}
    public async Task DeleteAsync(string key,CancellationToken token=default)
    {if(OperatingSystem.IsWindows())await Task.Run(()=>{if(!CredDeleteW(Target(key),1,0)&&Marshal.GetLastWin32Error()!=1168)throw Unavailable();},token);else await LinuxAsync("clear",key,null,token);}
    private static string Target(string key)=>"EnbNext/"+key;
    private static IOException Unavailable()=>new("The OS secret store is unavailable. Manual login remains available; no password was written to a settings file.");
    private static string? ReadWindows(string key)
    {
        if(!CredReadW(Target(key),1,0,out var pointer))
        {if(Marshal.GetLastWin32Error()==1168)return null;throw Unavailable();}
        try
        {
            var credential=Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob,(int)credential.BlobSize/2);
        }
        finally{CredFree(pointer);}
    }
    private static void WriteWindows(string key,string value)
    {
        int size=Encoding.Unicode.GetByteCount(value);
        if(size>2560)throw new IOException("The remembered login is too large for the OS secret store.");
        nint blob=Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential=new Credential{Type=1,TargetName=Target(key),BlobSize=(uint)size,Blob=blob,Persist=2,UserName="EnbNext"};
            if(!CredWriteW(ref credential,0))throw Unavailable();
        }
        finally{Marshal.ZeroFreeCoTaskMemUnicode(blob);}
    }
    private static async Task<string?> LinuxAsync(string command,string key,string? value,CancellationToken token)
    {
        var start=new ProcessStartInfo("secret-tool"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(command);
        if(command=="store")start.ArgumentList.Add("--label=EnbNext saved login");
        start.ArgumentList.Add("application");start.ArgumentList.Add("EnbNext");start.ArgumentList.Add("key");start.ArgumentList.Add(key);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        Process? process=null;
        try
        {
            process=Process.Start(start)??throw Unavailable();
            var output=process.StandardOutput.ReadToEndAsync(deadline.Token);var error=process.StandardError.ReadToEndAsync(deadline.Token);
            if(value is not null)await process.StandardInput.WriteAsync(value.AsMemory(),deadline.Token);
            process.StandardInput.Close();await process.WaitForExitAsync(deadline.Token);
            string result=await output,problem=await error;
            if(process.ExitCode!=0 && !(command is "lookup" or "clear" && process.ExitCode==1 && problem.Length==0))throw Unavailable();
            return command=="lookup"&&process.ExitCode==0?result.TrimEnd('\n'):null;
        }
        catch(Win32Exception){throw new IOException("Remembering logins needs the libsecret-tools package.");}
        finally{if(process is not null){if(!process.HasExited)process.Kill();process.Dispose();}}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags,Type;
        public string? TargetName,Comment;
        public long LastWritten;
        public uint BlobSize;public nint Blob;
        public uint Persist,AttributeCount;public nint Attributes;
        public string? TargetAlias,UserName;
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target,uint type,uint flags,out nint credential);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential,uint flags);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target,uint type,uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
