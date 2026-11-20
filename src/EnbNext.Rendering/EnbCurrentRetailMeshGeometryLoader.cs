using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Modern009BAA00 geometry handoff, including its postload tree
/// order. Material descriptor preprocessing remains with the material owner.</summary>
public static class EnbCurrentRetailMeshGeometryLoader
{
    /// <summary>009BABB6..009BACAD, before child loading. The image selector
    /// is supplied explicitly; the current retail image initializes it to1.</summary>
    public static uint TranslateInitialFlags(uint version,uint attributes,int imageSelector)
    {
        uint flags=0;
        if(version>=0x40001)
        {
            switch(attributes&0xff0000)
            {
                case 0x10000:flags|=0x200;break;
                case 0x20000:flags|=0x10400;break;
                case 0x60000:flags|=0x800;break;
            }
        }
        if((attributes&0x2000)!=0)flags|=0x100;
        if((attributes&0x8000)!=0)flags|=0x1000;
        if((attributes&0x20000000)!=0)flags|=0x10000;
        if((attributes&0x0f000000)!=0)
        {
            if(imageSelector==2&&(attributes&0x08000000)!=0)flags|=0x8000;
            else if((imageSelector==1||imageSelector==2)&&(attributes&0x04000000)!=0)flags|=0x4000;
            else if((imageSelector==0||imageSelector==1||imageSelector==2)&&(attributes&0x02000000)!=0)flags|=0x2000;
        }
        else if((attributes&0x4000)!=0)flags|=0x4000;
        return flags;
    }

    /// <summary>Requires an explicit authored-tree storage owner whenever0090
    /// is present. Trees built for collision attributes consume RNG before
    ///009BCC40 drops the tree for internal flag0400.</summary>
    public static EnbCurrentRetailMeshGeometryState Load(EnbW3dMesh mesh,int imageSelector,
        ref uint crtThreadSeed,Func<IReadOnlyList<byte>,EnbCurrentRetailMeshAabTree>? authoredTreeLoader=null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if(mesh.Vertices.Count==0||mesh.Triangles.Count==0)
            throw new ArgumentException("Decoded geometry handoff requires the nonempty buffers allocated by0092A450.");
        uint flags=TranslateInitialFlags(mesh.Version,mesh.Attributes,imageSelector);
        //0092D2B0 sets0400 after successfully reading every influence record,
        //independently of MeshHeader3's geometry-type attribute family.
        if(mesh.VertexInfluences.Count!=0)flags|=0x400;
        var triangles=mesh.Triangles.SelectMany(triangle=>new[]{unchecked((ushort)triangle.Vertex0),unchecked((ushort)triangle.Vertex1),unchecked((ushort)triangle.Vertex2)}).ToArray();
        EnbCurrentRetailMeshAabTree? tree=null;
        if(mesh.AuthoredAabTreePayload is {} payload)
        {
            if(authoredTreeLoader is null)throw new ArgumentException("Authored AAB loading requires its explicit incoming storage owner.");
            tree=authoredTreeLoader(payload)??throw new ArgumentException("Authored tree owner returned no tree.");
        }
        if((mesh.Attributes&0xff0)!=0&&tree is null)
            tree=EnbCurrentRetailMeshAabTree.Build(mesh.Vertices.ToArray(),triangles,ref crtThreadSeed);
        if((flags&0x400)!=0)tree=null;
        var normals=mesh.AuthoredNormalChunkType is null?new System.Numerics.Vector3[mesh.Vertices.Count]:mesh.Normals.ToArray();
        return new(mesh.Vertices.ToArray(),normals,triangles,mesh.AuthoredVertexJoinMap?.ToArray()??[],
            mesh.BoundingBoxMin,mesh.BoundingBoxMax,mesh.BoundingSphereCenter,mesh.BoundingRadius,flags,tree);
    }
}
