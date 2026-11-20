using System.Numerics;
using System.Buffers.Binary;
using Register=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public readonly record struct EnbCurrentRetailMeshAabNode(Vector3 Minimum,Vector3 Maximum,uint First,uint Second);

/// <summary>009B4CA0/009B5060 builder and009B2EF0 compact tree. The supplied
/// CRT thread seed is consumed in retail candidate/recursion order.</summary>
public sealed class EnbCurrentRetailMeshAabTree
{
    public IReadOnlyList<EnbCurrentRetailMeshAabNode> Nodes { get; }
    public IReadOnlyList<uint> PolygonIndices { get; }

    private EnbCurrentRetailMeshAabTree(EnbCurrentRetailMeshAabNode[] nodes,uint[] polygons)
    {
        Nodes=Array.AsReadOnly(nodes);PolygonIndices=Array.AsReadOnly(polygons);
    }

    public EnbCurrentRetailMeshAabTree Copy()=>new(Nodes.ToArray(),PolygonIndices.ToArray());

    /// <summary>Bind an authored tree whose output is fully determined by its
    /// bytes. Reject a dependency on unknown incoming native allocation/stack
    /// contents; do not replace that dependency with zero-filled geometry.</summary>
    public static EnbCurrentRetailMeshAabTree LoadOwnedAuthored(ReadOnlySpan<byte> payload)
    {
        int offset=0,nodeCount=0,polygonCount=0,nodeBytes=0,polygonBytes=0;
        bool first=true,nodesDefined=false,polygonsDefined=false;
        while(offset<payload.Length)
        {
            if(payload.Length-offset<8)throw new InvalidDataException("Incomplete AAB child header.");
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset+4)..])&0x7fffffff;
            if(size>payload.Length-offset-8)throw new InvalidDataException("AAB child exceeds source bytes.");
            int start=offset+8,length=(int)size;
            if(first)
            {
                if(length<32)throw new InvalidDataException("AAB header depends on incoming native stack bytes.");
                nodeCount=BinaryPrimitives.ReadInt32LittleEndian(payload[start..]);
                polygonCount=BinaryPrimitives.ReadInt32LittleEndian(payload[(start+4)..]);
                if(nodeCount<0||polygonCount<0)throw new InvalidDataException("Negative AAB allocation count.");
                nodeBytes=checked(nodeCount*32);polygonBytes=checked(polygonCount*4);
                nodesDefined=nodeCount==0;polygonsDefined=polygonCount==0;first=false;
            }
            else if(type==0x92&&length>=polygonBytes)polygonsDefined=true;
            else if(type==0x93&&nodeCount>0)
            {
                //After the first full record, a failed read repeats the last
                //record, as009B4A90 does; those repeated bytes are determined.
                if(length<32)throw new InvalidDataException("AAB nodes depend on incoming native stack bytes.");
                nodesDefined=true;
            }
            offset=checked(start+length);
        }
        if(first||!nodesDefined||!polygonsDefined)
            throw new InvalidDataException("AAB output depends on uninitialized native storage.");
        return LoadAuthored(payload,new byte[32],new byte[nodeBytes],new byte[polygonBytes],_=>new byte[32]);
    }

    /// <summary>009B4970/009B4A90 on a structurally complete child stream.
    /// Incoming allocations and the stack record at each node-child entry are
    /// supplied explicitly.009D4540 refuses reads crossing a child boundary;
    /// ignored failures retain whole incoming records, not zero-filled tails.
    /// Structural/storage checks contain unsafe native accesses.</summary>
    public static EnbCurrentRetailMeshAabTree LoadAuthored(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> incomingHeader,
        ReadOnlySpan<byte> incomingNodes, ReadOnlySpan<byte> incomingPolygons,
        Func<int, ReadOnlyMemory<byte>> incomingNodeRecord)
    {
        ArgumentNullException.ThrowIfNull(incomingNodeRecord);
        if(incomingHeader.Length!=32)throw new ArgumentException("Header stack storage must contain32 bytes.");
        var children=new List<(uint Type,int Start,int Length)>();
        for(int offset=0;offset<payload.Length;)
        {
            if(payload.Length-offset<8)throw new ArgumentException("Incomplete child header.");
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset,4));
            uint length=BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset+4,4))&0x7fffffff;
            if(length>(uint)(payload.Length-offset-8))throw new ArgumentException("Child exceeds supplied source storage.");
            children.Add((type,offset+8,(int)length));offset+=8+(int)length;
        }
        if(children.Count==0)throw new ArgumentException("The supplied stream must have a first child.");
        var header=incomingHeader.ToArray();
        var first=children[0];
        if(first.Length>=32)payload.Slice(first.Start,32).CopyTo(header);
        int nodeCount=BinaryPrimitives.ReadInt32LittleEndian(header);
        int polygonCount=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if(nodeCount<0||polygonCount<0||(long)nodeCount*32>incomingNodes.Length||(long)polygonCount*4>incomingPolygons.Length)
            throw new ArgumentException("Authored counts exceed supplied allocation storage.");
        var nodeBytes=incomingNodes.Slice(0,nodeCount*32).ToArray();
        var polygonBytes=incomingPolygons.Slice(0,polygonCount*4).ToArray();
        for(int ordinal=1;ordinal<children.Count;ordinal++)
        {
            var child=children[ordinal];
            if(child.Type==0x92)
            {
                if(child.Length>=polygonBytes.Length)payload.Slice(child.Start,polygonBytes.Length).CopyTo(polygonBytes);
            }
            else if(child.Type==0x93&&nodeCount>0)
            {
                var incoming=incomingNodeRecord(ordinal);
                if(incoming.Length!=32)throw new ArgumentException("Node stack storage must contain32 bytes.");
                var record=incoming.ToArray();int consumed=0;
                for(int node=0;node<nodeCount;node++)
                {
                    if(child.Length-consumed>=32){payload.Slice(child.Start+consumed,32).CopyTo(record);consumed+=32;}
                    record.CopyTo(nodeBytes,node*32);
                    //FLD/FSTP quiets a signaling NaN in each bounds word.
                    for(int word=0;word<6;word++)
                    {
                        var target=nodeBytes.AsSpan(node*32+word*4,4);
                        uint bits=BinaryPrimitives.ReadUInt32LittleEndian(target);
                        if((bits&0x7f800000)==0x7f800000&&(bits&0x007fffff)!=0)
                            BinaryPrimitives.WriteUInt32LittleEndian(target,bits|0x00400000);
                    }
                }
            }
        }
        var nodes=new EnbCurrentRetailMeshAabNode[nodeCount];
        for(int index=0;index<nodes.Length;index++)
        {
            var bytes=nodeBytes.AsSpan(index*32,32);
            nodes[index]=new(new(ReadFloat(bytes,0),ReadFloat(bytes,4),ReadFloat(bytes,8)),
                new(ReadFloat(bytes,12),ReadFloat(bytes,16),ReadFloat(bytes,20)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24)),BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28)));
        }
        var polygons=new uint[polygonCount];
        for(int index=0;index<polygons.Length;index++)polygons[index]=BinaryPrimitives.ReadUInt32LittleEndian(polygonBytes.AsSpan(index*4,4));
        return new(nodes,polygons);
    }

    private static float ReadFloat(ReadOnlySpan<byte> bytes,int offset)=>BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset,4)));

    public static EnbCurrentRetailMeshAabTree Build(ReadOnlySpan<Vector3> positions,ReadOnlySpan<ushort> triangleIndices,ref uint crtThreadSeed)
    {
        if(triangleIndices.Length%3!=0)throw new ArgumentException("Triangle storage is incomplete.");
        foreach(ushort index in triangleIndices)if(index>=positions.Length)throw new ArgumentException("Triangle index exceeds model storage.");
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var builder=new Builder(positions.ToArray(),triangleIndices.ToArray(),crtThreadSeed);
        var root=builder.Split(Enumerable.Range(0,triangleIndices.Length/3).Select(index=>(uint)index).ToArray());
        builder.SetBounds(root);
        var nodes=new List<EnbCurrentRetailMeshAabNode>();var polygons=new List<uint>();
        Flatten(root,nodes,polygons);
        crtThreadSeed=builder.Seed;
        return new(nodes.ToArray(),polygons.ToArray());
    }

    /// <summary>009B3000/009B3100: independent copied bounds, signed scalar
    /// multiplication without min/max repair; polygon and child IDs retained.</summary>
    public EnbCurrentRetailMeshAabTree CopyScaled(float scale)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        return new(Nodes.Select(node=>node with {Minimum=Multiply(node.Minimum,scale),Maximum=Multiply(node.Maximum,scale)}).ToArray(),PolygonIndices.ToArray());
    }

    private static Vector3 Multiply(Vector3 value,float scale)=>new((float)((Register)scale*value.X),(float)((Register)scale*value.Y),(float)((Register)scale*value.Z));
    private static int Flatten(Node node,List<EnbCurrentRetailMeshAabNode> nodes,List<uint> polygons)
    {
        int index=nodes.Count;nodes.Add(default);
        uint first,second;
        if(node.Left is null)
        {
            first=(uint)polygons.Count|0x80000000;second=(uint)node.Polygons.Length;
            polygons.AddRange(node.Polygons);
        }
        else
        {
            first=(uint)Flatten(node.Left,nodes,polygons);
            second=(uint)Flatten(node.Right!,nodes,polygons);
        }
        nodes[index]=new(node.Minimum,node.Maximum,first,second);
        return index;
    }

    private sealed class Node(uint[] polygons)
    {
        public uint[] Polygons=polygons;
        public Node? Left,Right;
        public Vector3 Minimum,Maximum;
    }

    private sealed class Builder(Vector3[] positions,ushort[] triangles,uint seed)
    {
        public uint Seed=seed;
        private static readonly float PlaneTolerance=BitConverter.UInt32BitsToSingle(0x3a83126f);
        private static readonly float BoundsPadding=BitConverter.UInt32BitsToSingle(0x38d1b717);

        private uint Next()
        {
            Seed=unchecked(Seed*0x343fd+0x269ec3);return (Seed>>16)&0x7fff;
        }
        private float Coordinate(uint polygon,int corner,int axis)
        {
            var value=positions[triangles[(int)polygon*3+corner]];
            return axis==0?value.X:axis==1?value.Y:value.Z;
        }
        private int Classify(uint polygon,int axis,float plane)
        {
            uint flags=0;
            for(int corner=0;corner<3;corner++)
            {
                Register distance=(Register)Coordinate(polygon,corner,axis)-plane;
                if(distance>PlaneTolerance)flags|=1;
                if(!(distance>=-PlaneTolerance))flags|=2; //FCOMP unordered also sets C0.
                flags|=4;
            }
            if(flags==4)return 4;
            if((flags&0xfffffffa)==0)return 1;
            return (flags&0xfffffff9)!=0?8:2;
        }
        public Node Split(uint[] polygons)
        {
            var node=new Node(polygons);
            if(polygons.Length<5)return node;
            float best=float.MaxValue,bestPlane=0;int bestAxis=0,bestLeft=0,bestRight=0;
            for(int trial=0;trial<Math.Min(50,polygons.Length);trial++)
            {
                uint polygon=polygons[Next()%(uint)polygons.Length];int corner=(int)(Next()%3),axis=(int)(Next()%3);
                float plane=Coordinate(polygon,corner,axis);
                var positiveMin=new Vector3(100000);var positiveMax=new Vector3(-100000);
                var negativeMin=positiveMin;var negativeMax=positiveMax;
                int left=0,right=0;
                foreach(uint candidate in polygons)
                {
                    int side=Classify(candidate,axis,plane);
                    if(side is 1 or 4 or 8){left++;Include(candidate,ref positiveMin,ref positiveMax);}
                    else if(side==2){right++;Include(candidate,ref negativeMin,ref negativeMax);}
                }
                //009B5590 expands only the negative-side box. Its X minimum
                //stays in an x87 register until the score has been calculated.
                Register minimumX=(Register)negativeMin.X-BoundsPadding;
                negativeMin.Y=(float)((Register)negativeMin.Y-BoundsPadding);
                negativeMin.Z=(float)((Register)negativeMin.Z-BoundsPadding);
                negativeMax.X=(float)((Register)negativeMax.X+BoundsPadding);
                negativeMax.Y=(float)((Register)negativeMax.Y+BoundsPadding);
                negativeMax.Z=(float)((Register)negativeMax.Z+BoundsPadding);
                Register positiveVolume=((Register)positiveMax.Z-positiveMin.Z)*((Register)positiveMax.Y-positiveMin.Y)*((Register)positiveMax.X-positiveMin.X)*left;
                Register negativeVolume=((Register)negativeMax.Z-negativeMin.Z)*((Register)negativeMax.Y-negativeMin.Y)*((Register)negativeMax.X-minimumX)*right;
                float score=left==0||right==0?float.MaxValue:(float)(positiveVolume+negativeVolume);
                if(!(score>=best)){best=score;bestPlane=plane;bestAxis=axis;bestLeft=left;bestRight=right;}
            }
            if(bestLeft+bestRight!=polygons.Length)return node;
            var leftPolygons=new List<uint>(bestLeft);var rightPolygons=new List<uint>(bestRight);
            foreach(uint polygon in polygons)
            {
                int side=Classify(polygon,bestAxis,bestPlane);
                if(side is 1 or 4 or 8)leftPolygons.Add(polygon);
                else if(side==2)rightPolygons.Add(polygon);
            }
            node.Polygons=[];
            if(leftPolygons.Count!=0)node.Left=Split(leftPolygons.ToArray());
            if(rightPolygons.Count!=0)node.Right=Split(rightPolygons.ToArray());
            return node;
        }
        private static void IncludePoint(Vector3 value,ref Vector3 minimum,ref Vector3 maximum)
        {
            //009B5C50's minima consume C0; maxima require ordered greater.
            if(!(value.X>=minimum.X))minimum.X=value.X;
            if(!(value.Y>=minimum.Y))minimum.Y=value.Y;
            if(!(value.Z>=minimum.Z))minimum.Z=value.Z;
            if(value.X>maximum.X)maximum.X=value.X;
            if(value.Y>maximum.Y)maximum.Y=value.Y;
            if(value.Z>maximum.Z)maximum.Z=value.Z;
        }
        private void Include(uint polygon,ref Vector3 minimum,ref Vector3 maximum)
        {
            for(int corner=0;corner<3;corner++)IncludePoint(positions[triangles[(int)polygon*3+corner]],ref minimum,ref maximum);
        }
        public void SetBounds(Node node)
        {
            if(node.Left is not null)SetBounds(node.Left);
            if(node.Right is not null)SetBounds(node.Right);
            var minimum=new Vector3(float.MaxValue);var maximum=new Vector3(-float.MaxValue);
            foreach(uint polygon in node.Polygons)Include(polygon,ref minimum,ref maximum);
            if(node.Left is not null)IncludeChild(node.Left,ref minimum,ref maximum);
            if(node.Right is not null)IncludeChild(node.Right,ref minimum,ref maximum);
            node.Minimum=minimum;node.Maximum=maximum;
        }
        private static void IncludeChild(Node child,ref Vector3 minimum,ref Vector3 maximum)
        {
            if(!(child.Minimum.X>=minimum.X))minimum.X=child.Minimum.X;
            if(!(child.Minimum.Y>=minimum.Y))minimum.Y=child.Minimum.Y;
            if(!(child.Minimum.Z>=minimum.Z))minimum.Z=child.Minimum.Z;
            if(child.Maximum.X>maximum.X)maximum.X=child.Maximum.X;
            if(child.Maximum.Y>maximum.Y)maximum.Y=child.Maximum.Y;
            if(child.Maximum.Z>maximum.Z)maximum.Z=child.Maximum.Z;
        }
    }
}
