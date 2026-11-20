using System.Numerics;
using System.Collections.ObjectModel;

namespace EnbNext.Rendering;

/// <summary>Explicit MeshModel base storage.0092A200 shares vector/index
/// buffers and copies the tree;009596E0 always separates vertices/normals
/// before0092D460. Authored inputs and initial flags belong to the loader.</summary>
public sealed class EnbCurrentRetailMeshGeometryState
{
    private Vector3[] _positions,_normals;
    private readonly ushort[] _triangles;
    private readonly uint[] _joinMap;
    private IReadOnlyList<Vector3> _positionView,_normalView;
    private Vector3 _minimum,_maximum,_center;
    private float _radius;
    private uint _flags;
    private EnbCurrentRetailMeshAabTree? _tree;

    public EnbCurrentRetailMeshGeometryState(ReadOnlySpan<Vector3> positions,ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<ushort> triangles,ReadOnlySpan<uint> joinMap,Vector3 minimum,Vector3 maximum,Vector3 center,
        float radius,uint flags,EnbCurrentRetailMeshAabTree? tree=null)
    {
        if(normals.Length<positions.Length||triangles.Length%3!=0||(!joinMap.IsEmpty&&joinMap.Length<positions.Length))
            throw new ArgumentException("Geometry storage is incomplete.");
        foreach(ushort index in triangles)if(index>=positions.Length)throw new ArgumentException("Triangle exceeds vertex storage.");
        if(!joinMap.IsEmpty)foreach(uint index in joinMap[..positions.Length])if(index>=positions.Length)throw new ArgumentException("Join map exceeds vertex storage.");
        _positions=positions.ToArray();_normals=normals.ToArray();_triangles=triangles.ToArray();_joinMap=joinMap.ToArray();
        _positionView=new ReadOnlyCollection<Vector3>(_positions);_normalView=new ReadOnlyCollection<Vector3>(_normals);
        _minimum=minimum;_maximum=maximum;_center=center;_radius=radius;_flags=flags;_tree=tree;
    }
    private EnbCurrentRetailMeshGeometryState(EnbCurrentRetailMeshGeometryState source)
    {
        _positions=source._positions;_normals=source._normals;_triangles=source._triangles;_joinMap=source._joinMap;
        _positionView=source._positionView;_normalView=source._normalView;
        _minimum=source._minimum;_maximum=source._maximum;_center=source._center;_radius=source._radius;
        _flags=source._flags;_tree=source._tree?.Copy();
    }
    public IReadOnlyList<Vector3> Positions=>_positionView;
    internal ulong VectorRevision {get;private set;}
    public ReadOnlySpan<ushort> TriangleIndices=>_triangles;
    public IReadOnlyList<Vector3> Normals
    {
        get
        {
            //0092C220 uses a shared scratch plane array, not model-owned
            //cached planes. This supplied temporary span has the same role.
            if((_flags&4)!=0)EnbCurrentRetailMeshGeometryRuntime.GetNormals(_positions,_triangles,_joinMap,
                _normals,new Vector4[_triangles.Length/3],ref _flags);
            return _normalView;
        }
    }
    public Vector3 Minimum=>_minimum;
    public Vector3 Maximum=>_maximum;
    public Vector3 Center=>_center;
    public float Radius=>_radius;
    public uint Flags=>_flags;
    public EnbCurrentRetailMeshAabTree? Tree=>_tree;
    public EnbCurrentRetailMeshGeometryState CopyModel()=>new(this);
    public void Scale(Vector3 scale,ref uint crtThreadSeed)
    {
        SeparateVectorStorage();
        ScaleSeparatedVectors(scale,ref crtThreadSeed);
    }
    internal void ScaleSeparatedVectors(Vector3 scale,ref uint crtThreadSeed)
    {
        VectorRevision++;
        EnbCurrentRetailMeshGeometryRuntime.Scale(_positions,_triangles,scale,ref _minimum,ref _maximum,
            ref _center,ref _radius,ref _flags,ref _tree,ref crtThreadSeed);
    }
    public void SeparateVectorStorage()
    {
        _positions=_positions.ToArray();_normals=_normals.ToArray();
        _positionView=new ReadOnlyCollection<Vector3>(_positions);_normalView=new ReadOnlyCollection<Vector3>(_normals);
    }

    /// <summary>0048E9D0's concrete class0 Mesh path.0093F370 computes the
    /// signed low32 pass/triangle product. Tree creation precedes RenderObj
    /// class-mask gates; it mutates the shared model without forcing a copy.</summary>
    public void PostprocessConfiguredMesh(int descriptorPassCount,
        ref EnbCurrentRetailRenderObjectState renderObject,ref uint crtThreadSeed)
    {
        PostprocessFactoryTree(descriptorPassCount,ref crtThreadSeed);
        if(!renderObject.HasBit4000&&(renderObject.LowByteClassMask&6)==0)
            renderObject=renderObject.WithLowByteClassMask(0x10);
    }

    /// <summary>The model mutation performed before48E9D0's per-RenderObj
    /// mask gates. Ordinary named child construction shares this model.</summary>
    public void PostprocessFactoryTree(int descriptorPassCount,ref uint crtThreadSeed)
    {
        int cost=unchecked(descriptorPassCount*(_triangles.Length/3));
        if(cost>30&&_tree is null&&(_flags&0x400)==0)
            _tree=EnbCurrentRetailMeshAabTree.Build(_positions,_triangles,ref crtThreadSeed);
    }
}
