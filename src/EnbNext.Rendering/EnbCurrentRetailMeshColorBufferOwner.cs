using System.Collections.ObjectModel;

namespace EnbNext.Rendering;

/// <summary>Reference-counted DWORD color storage shared by00946230 descriptor
/// copies.009473F0 separates only non-null storage with more than one reference.</summary>
public sealed class EnbCurrentRetailMeshColorBufferOwner : IDisposable
{
    private sealed class Storage(uint[] words)
    {
        public int References=1;
        public uint[] Words=words;
        public IReadOnlyList<uint> View=new ReadOnlyCollection<uint>(words);
    }
    private Storage? _storage;
    public EnbCurrentRetailMeshColorBufferOwner(ReadOnlySpan<uint> words)=>_storage=new(words.ToArray());
    private EnbCurrentRetailMeshColorBufferOwner(Storage storage){_storage=storage;storage.References++;}
    private Storage Current=>_storage??throw new ObjectDisposedException(nameof(EnbCurrentRetailMeshColorBufferOwner));
    public object StorageIdentity=>Current;
    public IReadOnlyList<uint> Words=>Current.View;
    public EnbCurrentRetailMeshColorBufferOwner Retain()=>new(Current);
    public void EnsureUnique()
    {
        var old=Current;if(old.References<=1)return;
        _storage=new(old.Words.ToArray());old.References--;
    }
    /// <summary>0048D1F0 writes the header vertex count in place after its
    /// preparation. No implicit copy occurs here; aliased unprepared buffers
    /// observe the writes. Extra storage beyond that count survives.</summary>
    public void ShiftHsv(int vertexCount,System.Numerics.Vector3 shift,bool conditionalMoveClamp)
    {
        var storage=Current;
        if(vertexCount<0||vertexCount>storage.Words.Length)throw new ArgumentOutOfRangeException(nameof(vertexCount));
        for(int vertex=0;vertex<vertexCount;vertex++)
            storage.Words[vertex]=EnbNext.Assets.EnbCurrentRetailHsvRuntime.ShiftMeshColor(storage.Words[vertex],shift,conditionalMoveClamp);
    }
    public void Dispose(){if(_storage is not {} storage)return;_storage=null;storage.References--;}
}
