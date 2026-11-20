using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>009D7A60/009D8030's single-package source. Resource lookup uses
/// the stored checksum directory, independently of filename enumeration.</summary>
public sealed class EnbCurrentRetailMixSource
{
    private readonly Entry[] _entries;
    private readonly byte[]? _bytes;
    private readonly string? _path;
    private readonly record struct Entry(uint Hash,uint Offset,uint Length);
    private EnbCurrentRetailMixSource(byte[] bytes,Entry[] entries,IReadOnlyList<string> names)
        =>(_bytes,_entries,Names)=(bytes,entries,names);
    private EnbCurrentRetailMixSource(string path,Entry[] entries,IReadOnlyList<string> names)
        =>(_path,_entries,Names)=(path,entries,names);
    public IReadOnlyList<string> Names { get; }

    /// <summary>Current retail startup retains CRT's C locale: all29 active
    /// locale ctor calls pass00B6E904="C"; named/global locale entry points
    /// have no branch or pointer consumers in the current binary.</summary>
    public static int ConvertStartupHashCharacter(sbyte value)
        =>value is >=97 and <=122?value-32:value;

    /// <summary>009E53A0. Character conversion is explicit because00A2850B
    /// uses the current CRT locale; callers must not substitute Unicode folding.</summary>
    public static uint ComputeNameHash(ReadOnlySpan<byte> name,Func<sbyte,int> convertCharacter,uint seed=0)
    {
        ArgumentNullException.ThrowIfNull(convertCharacter);
        uint crc=~seed;
        foreach(byte value in name)
        {
            if(value==0)break;
            crc^=(byte)convertCharacter(unchecked((sbyte)value));
            for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);
        }
        return ~crc;
    }

    /// <summary>Retain the directory and filename table, reopening the archive
    /// for each requested range as009D8030 does. No asset payload is preloaded.</summary>
    public static bool TryReadFile(string path,out EnbCurrentRetailMixSource? source)
    {
        source=null;
        try
        {
            using var file=File.OpenRead(EnbNext.Installation.EnbPathCase.Resolve(path));
            using var reader=new BinaryReader(file,Encoding.Latin1);
            if(file.Length<12 || reader.ReadUInt32()!=0x3158494d)return false;
            uint directory=reader.ReadUInt32(),namesOffset=reader.ReadUInt32();
            if(directory>file.Length-4 || namesOffset>file.Length-4)return false;
            file.Position=directory;
            uint count=reader.ReadUInt32();
            if(count>(file.Length-file.Position)/12 || count>int.MaxValue)return false;
            var entries=new Entry[(int)count];
            for(int i=0;i<entries.Length;i++)entries[i]=new(reader.ReadUInt32(),reader.ReadUInt32(),reader.ReadUInt32());
            file.Position=namesOffset;
            uint nameCount=reader.ReadUInt32();
            if(nameCount>file.Length-file.Position || nameCount>int.MaxValue)return false;
            var names=new List<string>((int)nameCount);
            for(uint i=0;i<nameCount;i++)
            {
                int length=reader.ReadByte();
                byte[] bytes=reader.ReadBytes(length);
                if(bytes.Length!=length)return false;
                int terminator=Array.IndexOf(bytes,(byte)0);
                names.Add(Encoding.Latin1.GetString(bytes,0,terminator<0?length:terminator));
            }
            source=new(path,entries,names.AsReadOnly());
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Raw009D8068..80C5 stops at the first midpoint hash equality.
    /// It does not compare filenames or scan other colliding entries.</summary>
    public bool TryReadResource(uint hash,out ReadOnlyMemory<byte> bytes)
        =>TryReadResource(hash,out bytes,out _,out _);

    public bool TryReadResource(uint hash,out ReadOnlyMemory<byte> bytes,out uint offset,out uint length)
    {
        bytes=default;
        offset=length=0;
        int first=0,count=_entries.Length;
        while(count>0)
        {
            int middle=count/2;
            var entry=_entries[first+middle];
            if(entry.Hash==hash)
            {
                if(_bytes is not null)
                {
                    if(entry.Offset>(uint)_bytes.Length || entry.Length>(uint)_bytes.Length-entry.Offset)return false;
                    bytes=_bytes.AsMemory((int)entry.Offset,(int)entry.Length);
                    offset=entry.Offset;length=entry.Length;
                    return true;
                }
                try
                {
                    using var file=File.OpenRead(EnbNext.Installation.EnbPathCase.Resolve(_path!));
                    if(entry.Offset>file.Length || entry.Length>file.Length-entry.Offset || entry.Length>int.MaxValue)return false;
                    file.Position=entry.Offset;
                    byte[] payload=new byte[(int)entry.Length];
                    file.ReadExactly(payload);
                    bytes=payload;
                    offset=entry.Offset;length=entry.Length;
                    return true;
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return false;
                }
            }
            if(entry.Hash>hash)count=middle;
            else{first+=middle+1;count-=middle+1;}
        }
        return false;
    }
}
