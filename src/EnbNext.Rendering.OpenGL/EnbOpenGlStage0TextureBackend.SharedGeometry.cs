using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

// Device-only lease. It retains authored streams, never an occurrence packet,
// mapper clock, scene owner or a first user's generation.
internal sealed class EnbOpenGlSharedGeometry(object owner, object key, int vertex, int uv, int index, int vao,
    ImmutableArray<float> vertices, EnbW3dUvStream coordinates,
    EnbOpenGlIndexElementType indexType, ImmutableArray<ushort> shortIndices, ImmutableArray<uint> intIndices)
{
    internal readonly object Owner=owner, Key=key;
    internal readonly int Vertex=vertex,Uv=uv,Index=index,Vao=vao;
    internal readonly ImmutableArray<float> Vertices=vertices;
    internal readonly EnbW3dUvStream Coordinates=coordinates;
    internal readonly EnbOpenGlIndexElementType IndexType=indexType;
    internal readonly ImmutableArray<ushort> ShortIndices=shortIndices;
    internal readonly ImmutableArray<uint> IntIndices=intIndices;
    internal int References=1;
    internal EnbOpenGlGeometryContainer? Container;
    internal int BaseVertex,IndexByteOffset;
}

internal sealed class EnbOpenGlGeometryContainer(int vertex,int uv,int index,int vao,int vertexCapacity,int indexCapacity)
{
    internal readonly int Vertex=vertex,Uv=uv,Index=index,Vao=vao,VertexCapacity=vertexCapacity,IndexCapacity=indexCapacity;
    internal int UsedVertices,UsedIndexBytes,Allocations;
}

public sealed partial class EnbOpenGlStage0TextureBackend
{
    private readonly List<EnbOpenGlGeometryContainer> _geometryContainers=[];
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGeometrySubDataProc(int target,IntPtr offset,IntPtr size,IntPtr data);
    private GlGeometrySubDataProc? _geometrySubData;
    private object _geometryDeviceOwner=new();
    private GlInterface? _geometryContext;
    private readonly Dictionary<object,List<EnbOpenGlSharedGeometry>> _sharedGeometry=new(ReferenceEqualityComparer.Instance);
    internal bool GeometrySharingEnabled {get;set;}=true;

    private static bool IsShareableGeometry(EnbW3dProgrammableTextureLanePlan lane, EnbW3dNPatchGeometry? patch)
        => patch is null && !lane.TextureCoordinates1.IsMutable && lane.Submission.VertexSource==EnbW3dSubmissionVertexSource.RigidMesh &&
            lane.Submission.Packet.CurrentRetailViewPositions is null &&
            lane.Submission.Packet.Mesh.CurrentRetailUsesMeshRenderer && !lane.RunStates.Any(UsesNullMaterialCoordinates);

    private EnbOpenGlSharedGeometry? AcquireSharedGeometry(EnbW3dProgrammableTextureLanePlan lane,
        EnbOpenGlLitShaderUploadPlan geometry)
    {
        if(!_sharedGeometry.TryGetValue(lane.Submission.Packet.Mesh.Positions,out var candidates))return null;
        foreach(var candidate in candidates)
        {
            var indices=geometry.IndexedPlan;
            // Compare the actual upload bytes only at registration. Different
            // materials/UV/color realizations must not alias the same buffer.
            if(candidate.IndexType!=indices.IndexElementType ||
                !MemoryMarshal.AsBytes(candidate.Vertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(geometry.VertexElements.AsSpan())) ||
                !MemoryMarshal.AsBytes(candidate.Coordinates.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(lane.TextureCoordinates1.AsSpan())) ||
                !candidate.ShortIndices.AsSpan().SequenceEqual(indices.UnsignedShortIndices.AsSpan()) ||
                !candidate.IntIndices.AsSpan().SequenceEqual(indices.UnsignedIntIndices.AsSpan()))continue;
            candidate.References++;
            return candidate;
        }
        return null;
    }

    private EnbOpenGlSharedGeometry RegisterSharedGeometry(EnbW3dProgrammableTextureLanePlan lane,
        EnbOpenGlLitShaderUploadPlan geometry,int vertex,int uv,int index,int vao)
    {
        var key=lane.Submission.Packet.Mesh.Positions;
        if(!_sharedGeometry.TryGetValue(key,out var candidates))_sharedGeometry.Add(key,candidates=[]);
        var indices=geometry.IndexedPlan;
        var shared=new EnbOpenGlSharedGeometry(_geometryDeviceOwner,key,vertex,uv,index,vao,geometry.VertexElements,
            lane.TextureCoordinates1,indices.IndexElementType,indices.UnsignedShortIndices,indices.UnsignedIntIndices);
        candidates.Add(shared);return shared;
    }

    private void ReleaseSharedGeometry(GlInterface gl,EnbOpenGlSharedGeometry shared)
    {
        if(!ReferenceEquals(shared.Owner,_geometryDeviceOwner) || --shared.References!=0)return;
        var candidates=_sharedGeometry[shared.Key];candidates.Remove(shared);
        if(candidates.Count==0)_sharedGeometry.Remove(shared.Key);
        if(shared.Container is {} container)
        {
            if(--container.Allocations!=0)return;
            _geometryContainers.Remove(container);
        }
        if(shared.Vao!=0 && gl.IsDeleteVertexArraysAvailable)gl.DeleteVertexArray(shared.Vao);
        gl.DeleteBuffer(shared.Vertex);gl.DeleteBuffer(shared.Uv);gl.DeleteBuffer(shared.Index);
    }

    private unsafe EnbOpenGlSharedGeometry CreateContainerGeometry(GlInterface gl,
        EnbW3dProgrammableTextureLanePlan lane,EnbOpenGlLitShaderUploadPlan geometry)
    {
        int vertices=geometry.VertexCount;
        var indices=geometry.IndexedPlan;
        int indexBytes=checked(indices.IndexCount*(indices.IndexElementType==EnbOpenGlIndexElementType.UnsignedShort?2:4));
        // Registration appends immutable ranges. Like retail's containers, a
        // container survives individual model removals and is released empty.
        EnbOpenGlGeometryContainer? container=null;
        foreach(var candidate in _geometryContainers)
            if(candidate.VertexCapacity-candidate.UsedVertices>=vertices && candidate.IndexCapacity-candidate.UsedIndexBytes>=indexBytes &&
                (indices.IndexElementType!=EnbOpenGlIndexElementType.UnsignedShort || (long)candidate.UsedVertices+vertices<=65536))
            {container=candidate;break;}
        bool created=container is null;
        int vertex=0,uv=0,index=0,vao=0;
        try
        {
            if(container is null)
            {
                int vertexCapacity=Math.Max(4000,vertices),indexCapacity=Math.Max(48000,indexBytes);
                vao=gl.GenVertexArray();gl.BindVertexArray(vao);
                vertex=gl.GenBuffer();gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,vertex);
                gl.BufferData(GlConsts.GL_ARRAY_BUFFER,new IntPtr(checked(vertexCapacity*EnbOpenGlLitShaderUploadPlan.StrideBytes)),IntPtr.Zero,GlConsts.GL_STATIC_DRAW);
                BindVertexLayout(gl);
                uv=gl.GenBuffer();gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,uv);
                gl.BufferData(GlConsts.GL_ARRAY_BUFFER,new IntPtr(checked(vertexCapacity*8)),IntPtr.Zero,GlConsts.GL_STATIC_DRAW);
                BindTextureCoordinate1Layout(gl);
                index=gl.GenBuffer();gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER,index);
                gl.BufferData(GlConsts.GL_ELEMENT_ARRAY_BUFFER,new IntPtr(indexCapacity),IntPtr.Zero,GlConsts.GL_STATIC_DRAW);
                container=new(vertex,uv,index,vao,vertexCapacity,indexCapacity);
            }
            else gl.BindVertexArray(container.Vao);
            void Upload(int target,int buffer,int offset,ReadOnlySpan<byte> bytes)
            {
                gl.BindBuffer(target,buffer);
                if(bytes.IsEmpty)return;
                fixed(byte* ptr=bytes)_geometrySubData!(target,new IntPtr(offset),new IntPtr(bytes.Length),(IntPtr)ptr);
            }
            Upload(GlConsts.GL_ARRAY_BUFFER,container.Vertex,checked(container.UsedVertices*EnbOpenGlLitShaderUploadPlan.StrideBytes),MemoryMarshal.AsBytes(geometry.VertexElements.AsSpan()));
            Upload(GlConsts.GL_ARRAY_BUFFER,container.Uv,checked(container.UsedVertices*8),MemoryMarshal.AsBytes(lane.TextureCoordinates1.AsSpan()));
            // GLES contexts do not universally support base-vertex draws.
            // Rebase once at registration, as part of copying the model into
            // its container, and retain ordinary DrawElements at submission.
            if(indices.IndexElementType==EnbOpenGlIndexElementType.UnsignedShort)
            {
                var rebased=System.Buffers.ArrayPool<ushort>.Shared.Rent(indices.IndexCount);
                try
                {
                    for(int i=0;i<indices.IndexCount;i++)rebased[i]=checked((ushort)(indices.UnsignedShortIndices[i]+container.UsedVertices));
                    Upload(GlConsts.GL_ELEMENT_ARRAY_BUFFER,container.Index,container.UsedIndexBytes,MemoryMarshal.AsBytes(rebased.AsSpan(0,indices.IndexCount)));
                }
                finally{System.Buffers.ArrayPool<ushort>.Shared.Return(rebased);}
            }
            else
            {
                var rebased=System.Buffers.ArrayPool<uint>.Shared.Rent(indices.IndexCount);
                try
                {
                    for(int i=0;i<indices.IndexCount;i++)rebased[i]=checked(indices.UnsignedIntIndices[i]+(uint)container.UsedVertices);
                    Upload(GlConsts.GL_ELEMENT_ARRAY_BUFFER,container.Index,container.UsedIndexBytes,MemoryMarshal.AsBytes(rebased.AsSpan(0,indices.IndexCount)));
                }
                finally{System.Buffers.ArrayPool<uint>.Shared.Return(rebased);}
            }
            gl.BindVertexArray(0);gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,0);
            int error=gl.GetError();
            if(error!=GlConsts.GL_NO_ERROR)throw new InvalidOperationException($"Geometry container registration failed: GL 0x{error:X4}.");
            var shared=RegisterSharedGeometry(lane,geometry,container.Vertex,container.Uv,container.Index,container.Vao);
            shared.Container=container;shared.BaseVertex=container.UsedVertices;shared.IndexByteOffset=container.UsedIndexBytes;
            container.UsedVertices+=vertices;
            // Align every allocation for either index width without changing
            // the model's own 16/32-bit index representation.
            container.UsedIndexBytes=(container.UsedIndexBytes+indexBytes+3)&~3;
            container.Allocations++;
            if(created)_geometryContainers.Add(container);
            vertex=uv=index=vao=0;
            return shared;
        }
        finally
        {
            if(vao!=0)gl.DeleteVertexArray(vao);
            if(vertex!=0)gl.DeleteBuffer(vertex);if(uv!=0)gl.DeleteBuffer(uv);if(index!=0)gl.DeleteBuffer(index);
        }
    }

    private void FinishPrivateGeometryMutation(GlInterface gl,EnbOpenGlStage0TextureMeshResource original,
        EnbOpenGlStage0TextureMeshResource updated)
    {
        if(ReferenceEquals(original,updated) || original.SharedGeometry is not {} shared)return;
        int error=gl.GetError();
        if(error!=GlConsts.GL_NO_ERROR)throw new InvalidOperationException($"Private geometry update failed: GL 0x{error:X4}.");
        ReleaseSharedGeometry(gl,shared);
    }

    private static void AbandonPrivateGeometryMutation(GlInterface gl,EnbOpenGlStage0TextureMeshResource original,
        EnbOpenGlStage0TextureMeshResource updated)
    {
        if(ReferenceEquals(original,updated) || original.SharedGeometry is null)return;
        if(updated.VertexArray!=0)gl.DeleteVertexArray(updated.VertexArray);
        gl.DeleteBuffer(updated.VertexBuffer);gl.DeleteBuffer(updated.TextureCoordinate1Buffer);gl.DeleteBuffer(updated.IndexBuffer);
    }

    private EnbOpenGlStage0TextureMeshResource EnsurePrivateGeometry(GlInterface gl,EnbOpenGlStage0TextureMeshResource resource)
    {
        if(resource.SharedGeometry is not {} shared)return resource;
        if(!ReferenceEquals(shared.Owner,_geometryDeviceOwner))throw new InvalidOperationException("Geometry belongs to a released device.");
        // A later explicit mutation detaches its buffers; other owners and
        // already queued occurrences continue using the original geometry.
        InterruptRetainedSubmission(materialChanged:false);
        int vertex=0,uv=0,index=0,vao=0;
        try
        {
            if(shared.Vao!=0){vao=gl.GenVertexArray();gl.BindVertexArray(vao);}
            vertex=gl.GenBuffer();gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,vertex);
            UploadArray(gl,GlConsts.GL_ARRAY_BUFFER,shared.Vertices.AsSpan(),GlStreamDraw);BindVertexLayout(gl);
            uv=gl.GenBuffer();gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,uv);
            UploadArray(gl,GlConsts.GL_ARRAY_BUFFER,MemoryMarshal.Cast<Vector2,float>(shared.Coordinates.AsSpan()),GlStreamDraw);
            BindTextureCoordinate1Layout(gl);
            index=gl.GenBuffer();gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER,index);
            if(shared.IndexType==EnbOpenGlIndexElementType.UnsignedShort)
                UploadArray(gl,GlConsts.GL_ELEMENT_ARRAY_BUFFER,shared.ShortIndices.AsSpan(),GlStreamDraw);
            else UploadArray(gl,GlConsts.GL_ELEMENT_ARRAY_BUFFER,shared.IntIndices.AsSpan(),GlStreamDraw);
            if(vao!=0)gl.BindVertexArray(0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,0);
            if(vao==0)gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER,0);
            var result=resource with {VertexBuffer=vertex,TextureCoordinate1Buffer=uv,IndexBuffer=index,VertexArray=vao,SharedGeometry=null};
            int error=gl.GetError();
            if(error!=GlConsts.GL_NO_ERROR)throw new InvalidOperationException($"Private geometry copy failed: GL 0x{error:X4}.");
            vertex=uv=index=vao=0;return result;
        }
        finally
        {
            if(vao!=0)gl.DeleteVertexArray(vao);
            if(vertex!=0)gl.DeleteBuffer(vertex);if(uv!=0)gl.DeleteBuffer(uv);if(index!=0)gl.DeleteBuffer(index);
        }
    }
}
