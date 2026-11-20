using System.Collections;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>View-owned temporary vertex storage. A frame owns every lease until
/// its final flush; nested/repeated views use distinct frames. Persistent caches
/// may retain range metadata, but may not read an expired frame's vertex data.</summary>
public sealed class EnbW3dTransientGeometryArena
{
    private readonly Stack<Storage> _available=[];
    internal sealed class Storage
    {
        internal readonly List<EnbW3dFrameBuffer<Vector3>> Positions=[];
        internal readonly List<EnbW3dFrameBuffer<EnbRenderColor32>> Colors=[];
    }
    public long ArrayAllocations {get;private set;}
    public Frame BeginFrame()
    {
        var storage=_available.TryPop(out var reusable)?reusable:new Storage();
        return new Frame(this,storage);
    }
    public sealed class Frame : IDisposable
    {
        private readonly EnbW3dTransientGeometryArena _arena;
        private readonly Storage _storage;
        private int _positionCount,_colorCount;
        private readonly EnbW3dFrameStreamEpoch _epoch=new();
        internal Frame(EnbW3dTransientGeometryArena arena,Storage storage){_arena=arena;_storage=storage;}
        private EnbW3dFrameStream<T> Rent<T>(List<EnbW3dFrameBuffer<T>> buffers,ref int used,int count) where T:unmanaged
        {
            if(_epoch is not {Active:true})throw new ObjectDisposedException(nameof(Frame));
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            int index=used++;
            if(index==buffers.Count)buffers.Add(new());
            var slot=buffers[index];
            var buffer=slot.Values;
            if(buffer.Length<count)
            {slot.Values=buffer=new T[Math.Max(count,Math.Max(16,buffer.Length*2))];_arena.ArrayAllocations++;}
            return new(buffer,count,_epoch,slot);
        }
        internal EnbW3dFrameStream<Vector3> RentPositions(int count)=>Rent(_storage.Positions,ref _positionCount,count);
        internal EnbW3dFrameStream<EnbRenderColor32> RentColors(int count)=>Rent(_storage.Colors,ref _colorCount,count);
        public void Dispose()
        {
            if(_epoch is not {Active:true})return;
            _epoch.Active=false;_arena._available.Push(_storage);
        }
    }
}

internal sealed class EnbW3dFrameBuffer<T> where T:unmanaged {internal T[] Values=[];}

internal sealed class EnbW3dFrameStreamEpoch {internal bool Active=true;}

/// <summary>A fixed range whose data is valid only until its frame is drained.
/// Count remains valid after drain for retained resource capacity accounting.</summary>
public sealed class EnbW3dFrameStream<T> : IReadOnlyList<T> where T:unmanaged
{
    private readonly T[] _values;
    private readonly EnbW3dFrameStreamEpoch _epoch;
    private EnbW3dFrameBuffer<T>? _storage;
    private bool _sealed;
    private bool _finiteVector3Proof;
    internal bool HasFiniteVector3Proof {get{RequireActive();return _finiteVector3Proof;}}
    public int Count {get;}
    internal bool IsActive => _epoch.Active;
    internal EnbW3dFrameStream(T[] values,int count,EnbW3dFrameStreamEpoch epoch,EnbW3dFrameBuffer<T> storage)
    {_values=values;Count=count;_epoch=epoch;_storage=storage;}
    private void RequireActive(){if(!_epoch.Active)throw new ObjectDisposedException(nameof(EnbW3dFrameStream<T>),"The draw frame has completed its final flush.");}
    private IReadOnlyList<T>? _publication;
    // This is an explicit cross-frame consumer, not an extension of the draw
    // lease. Already-published values remain available after drain; a first
    // publication must occur while the source range is still active.
    // The producer promises it has finished every write, including borrowed
    // spans. Sealed skin vertices can transfer their array to UI publication
    // instead of copying it. Unsealed producers keep the snapshot contract.
    internal void SealForPublication(){RequireActive();_sealed=true;}
    // Only a producer that has checked every Vector3 and finished all borrowed
    // writes may publish this proof. It expires with the frame, never with a mesh.
    internal void SealValidatedVector3()
    {
        if(typeof(T)!=typeof(Vector3))throw new InvalidOperationException("Finite proof requires Vector3 values.");
        SealForPublication();_finiteVector3Proof=true;
    }
    private sealed class Publication(T[] values,int count) : IReadOnlyList<T>
    {
        public int Count=>count;
        public T this[int index] => (uint)index<(uint)count?values[index]:throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<T> GetEnumerator(){for(int i=0;i<count;i++)yield return values[i];}
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    internal IReadOnlyList<T> RetainForPublication()
    {
        if(_publication is not null)return _publication;
        RequireActive();
        if(!_sealed)return _publication=Array.AsReadOnly(ReadSpan.ToArray());
        // Each range has its own slot. Removing this array before frame drain
        // prevents all later/nested views from recycling published vertices.
        _storage!.Values=[];
        _storage=null;
        return _publication=new Publication(_values,Count);
    }
    internal ReadOnlySpan<T> ReadSpan {get{RequireActive();return _values.AsSpan(0,Count);}}
    internal Span<T> WriteSpan {get{RequireActive();if(_sealed)throw new InvalidOperationException("The producer has sealed this stream for publication.");return _values.AsSpan(0,Count);}}
    public T this[int index]
    {
        get{RequireActive();if((uint)index>=(uint)Count)throw new ArgumentOutOfRangeException(nameof(index));return _values[index];}
    }
    public IEnumerator<T> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}
