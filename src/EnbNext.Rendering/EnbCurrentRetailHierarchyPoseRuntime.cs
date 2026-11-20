using System.Buffers.Binary;
using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public interface IEnbCurrentRetailHierarchyAggregatePoseProvider<T,TMask> where T:class where TMask:class
{
    int Count { get; }
    T BorrowedAnimation(int index);
    T? RetainedAnimation(int index);
    int PivotCount(T animation);
    double CurrentFrame(int index);
    TMask? RetainedMask(int index);
    double Weight(int index);
    float MaskWeight(TMask mask,int pivot);
    void ReleaseMask(TMask mask);
    Vector3 Translation(T animation,int pivot,uint frameWord);
    Quaternion Rotation(T animation,int pivot,uint frameWord);
    byte Visibility(T animation,int pivot,uint frameWord);
    void ReleaseAnimation(T animation);
    Quaternion Interpolate(Quaternion first,Quaternion second,float fraction);
}

/// <summary>009895B0 default pose,009C5120 captured overrides and008C2CD0
/// affine matrix product over retained raw hierarchy records.</summary>
public static class EnbCurrentRetailHierarchyPoseRuntime
{
    /// <summary>Retail hierarchy pivot record size.</summary>
    public const int RecordBytes = 0xb0;
    private static float Word(ReadOnlySpan<byte> bytes, int word)
        => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(word * 4)..]));
    private static void Store(Span<byte> bytes, int word, R value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes[(word * 4)..], BitConverter.SingleToUInt32Bits((float)value));

    public static void Multiply(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, Span<byte> destination,
        EnbRetailLinePrecision linePrecision=EnbRetailLinePrecision.Single24)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(linePrecision);
        Span<byte> retainedFirst = stackalloc byte[48]; first[..48].CopyTo(retainedFirst);
        for (int column = 0; column < 4; column++)
        {
            R x = Word(second, column), y = Word(second, 4 + column), z = Word(second, 8 + column);
            for (int row = 0; row < 3; row++)
            {
                R value = (x * Word(retainedFirst, row * 4) + z * Word(retainedFirst, row * 4 + 2))
                    + y * Word(retainedFirst, row * 4 + 1);
                if (column == 3) value += Word(retainedFirst, row * 4 + 3);
                Store(destination, row * 4 + column, value);
            }
        }
    }

    public static void ApplyOverride(Span<byte> record,EnbRetailLinePrecision linePrecision=EnbRetailLinePrecision.Single24)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(linePrecision);
        if (record[0xac] == 0)
        {
            Multiply(record[0x48..], record[0x7c..], record[0x48..],linePrecision);
            return;
        }
        Span<byte> basis = stackalloc byte[48]; record.Slice(0x7c, 48).CopyTo(basis);
        BinaryPrimitives.WriteUInt32LittleEndian(basis[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(basis[28..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(basis[44..], 0);
        Multiply(record[0x48..], basis, record[0x48..],linePrecision);
        for (int row = 0; row < 3; row++)
            Store(record[0x48..], row * 4 + 3, (R)Word(record[0x7c..], row * 4 + 3) + Word(record[0x48..], row * 4 + 3));
    }

    /// <summary>The root write occurs even for count0: the caller must supply
    /// the actual retained root storage. Parent addresses are resolved by the
    /// owning hierarchy without changing their authored ordering.</summary>
    public static void EvaluateDefault(ReadOnlySpan<byte> header, Span<byte> records, ReadOnlySpan<byte> rootMatrix,
        Func<uint, ReadOnlyMemory<byte>> retainedParentMatrix,
        EnbRetailLinePrecision linePrecision=EnbRetailLinePrecision.Single24)
    {
        rootMatrix[..48].CopyTo(records[0x48..]); records[0x78] = 1;
        for (int index = 1; index < BinaryPrimitives.ReadInt32LittleEndian(header[0x10..]); index++)
        {
            var record = records.Slice(index * RecordBytes,
                RecordBytes);
            var parent = retainedParentMatrix(BinaryPrimitives.ReadUInt32LittleEndian(record[0x14..]));
            Multiply(parent.Span, record[0x18..], record[0x48..],linePrecision);
            record[0x78] = 1;
            if (record[0x79] != 0) ApplyOverride(record,linePrecision);
        }
    }

    public static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> bytes)
        => new(Word(bytes,0),Word(bytes,4),Word(bytes,8),0,Word(bytes,1),Word(bytes,5),Word(bytes,9),0,
            Word(bytes,2),Word(bytes,6),Word(bytes,10),0,Word(bytes,3),Word(bytes,7),Word(bytes,11),1);

    private static void WriteMatrix(Span<byte> bytes, Matrix4x4 matrix)
    {
        float[] rows = [matrix.M11,matrix.M21,matrix.M31,matrix.M41,matrix.M12,matrix.M22,matrix.M32,matrix.M42,
            matrix.M13,matrix.M23,matrix.M33,matrix.M43];
        for (int word = 0; word < 12; word++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(word * 4)..],BitConverter.SingleToUInt32Bits(rows[word]));
    }

    /// <summary>Whole00989670 over concrete animation virtual consumers.
    /// Pivot count snapshots after the root write. Absent animation pivots
    /// preserve incoming visibility unless a captured override forces1.</summary>
    public static void EvaluateSingle(ReadOnlySpan<byte> header, Span<byte> records, ReadOnlySpan<byte> rootMatrix,
        Func<uint, ReadOnlyMemory<byte>> retainedParentMatrix, uint frameWord, Func<int> animationPivotCount,
        Func<int,uint,Vector3> translation, Func<int,uint,Quaternion> rotation, Func<int,uint,byte> visibility)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        rootMatrix[..48].CopyTo(records[0x48..]); records[0x78] = 1;
        int animatedPivots = animationPivotCount();
        for (int index = 1; index < BinaryPrimitives.ReadInt32LittleEndian(header[0x10..]); index++)
        {
            var record = records.Slice(index * RecordBytes,
                RecordBytes);
            var parent = retainedParentMatrix(BinaryPrimitives.ReadUInt32LittleEndian(record[0x14..]));
            Multiply(parent.Span,record[0x18..],record[0x48..]);
            if (index < animatedPivots)
            {
                Vector3 offset = translation(index,frameWord);
                float scale = Word(header,6);
                float x = (float)((R)offset.X * scale), y = (float)((R)offset.Y * scale);
                R z = (R)scale * offset.Z;
                for (int row = 0; row < 3; row++)
                    Store(record[0x48..],row * 4 + 3,
                        ((z * Word(record[0x48..],row * 4 + 2) + (R)y * Word(record[0x48..],row * 4 + 1))
                            + (R)x * Word(record[0x48..],row * 4)) + Word(record[0x48..],row * 4 + 3));
                Quaternion quaternion = rotation(index,frameWord);
                Matrix4x4 basis = EnbRetailParticleHierarchyTransform.QuaternionBasis(quaternion);
                WriteMatrix(record[0x48..],EnbRetailPointGroupTransform.Compose(basis,ReadMatrix(record[0x48..])));
                record[0x78] = visibility(index,frameWord);
            }
            if (record[0x79] != 0) { ApplyOverride(record); record[0x78] = 1; }
        }
    }

    /// <summary>Whole00989890. The smaller initial pivot count selects a
    /// third live query; the returned third count controls both animations.
    /// Visibility queries short circuit on the first returned low byte.</summary>
    public static void EvaluatePair(ReadOnlySpan<byte> header,Span<byte> records,ReadOnlySpan<byte> rootMatrix,
        Func<uint,ReadOnlyMemory<byte>> retainedParentMatrix,uint firstFrame,uint secondFrame,float weight,
        Func<int,int> animationPivotCount,Func<int,int,uint,Vector3> translation,
        Func<int,int,uint,Quaternion> rotation,Func<int,int,uint,byte> visibility,
        Func<Quaternion,Quaternion,float,Quaternion> interpolate)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        rootMatrix[..48].CopyTo(records[0x48..]);records[0x78]=1;
        int firstCount=animationPivotCount(0),secondCount=animationPivotCount(1);
        int animatedPivots=animationPivotCount(firstCount<secondCount?0:1);
        for(int index=1;index<BinaryPrimitives.ReadInt32LittleEndian(header[0x10..]);index++)
        {
            var record=records.Slice(index*RecordBytes,RecordBytes);
            Multiply(retainedParentMatrix(BinaryPrimitives.ReadUInt32LittleEndian(record[0x14..])).Span,record[0x18..],record[0x48..]);
            if(index<animatedPivots)
            {
                Vector3 first=translation(0,index,firstFrame),second=translation(1,index,secondFrame);
                float scale=Word(header,6);
                R secondX=(R)second.X*weight;
                float secondY=(float)((R)second.Y*weight),secondZ=(float)((R)second.Z*weight);
                R inverse=(R)1-weight;
                float firstX=(float)((R)first.X*inverse),firstY=(float)((R)first.Y*inverse),firstZ=(float)((R)first.Z*inverse);
                R x=(secondX+firstX)*scale;
                R y=(R)(float)((R)firstY+secondY)*scale,z=(R)(float)((R)firstZ+secondZ)*scale;
                for(int row=0;row<3;row++)Store(record[0x48..],row*4+3,
                    ((z*Word(record[0x48..],row*4+2)+y*Word(record[0x48..],row*4+1))+x*Word(record[0x48..],row*4))+Word(record[0x48..],row*4+3));
                Quaternion firstRotation=rotation(0,index,firstFrame),secondRotation=rotation(1,index,secondFrame);
                Quaternion blended=interpolate(firstRotation,secondRotation,weight);
                WriteMatrix(record[0x48..],EnbRetailPointGroupTransform.Compose(
                    EnbRetailParticleHierarchyTransform.QuaternionBasis(blended),ReadMatrix(record[0x48..])));
                record[0x78]=(byte)(visibility(0,index,firstFrame)!=0||visibility(1,index,secondFrame)!=0?1:0);
            }
            if(record[0x79]!=0){ApplyOverride(record);record[0x78]=1;}
        }
    }

    /// <summary>Whole00989BC0 using actual retained aggregate/mask consumers.
    /// Minimum pivot-count scan, transform accumulation and visibility scan
    /// each reread the aggregate count at their native loop boundaries.</summary>
    public static void EvaluateAggregate<T,TMask>(ReadOnlySpan<byte> header,Span<byte> records,ReadOnlySpan<byte> rootMatrix,
        Func<uint,ReadOnlyMemory<byte>> retainedParentMatrix,IEnbCurrentRetailHierarchyAggregatePoseProvider<T,TMask> owner)
        where T:class where TMask:class
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        rootMatrix[..48].CopyTo(records[0x48..]);records[0x78]=1;
        int animatedPivots=100000;
        for(int entry=0;entry<owner.Count;entry++)
        {
            int count=owner.PivotCount(owner.BorrowedAnimation(entry));
            if(count<=animatedPivots)animatedPivots=owner.PivotCount(owner.BorrowedAnimation(entry));
        }
        if(animatedPivots==100000)animatedPivots=0;
        for(int pivot=1;pivot<BinaryPrimitives.ReadInt32LittleEndian(header[0x10..]);pivot++)
        {
            var record=records.Slice(pivot*RecordBytes,RecordBytes);
            Multiply(retainedParentMatrix(BinaryPrimitives.ReadUInt32LittleEndian(record[0x14..])).Span,record[0x18..],record[0x48..]);
            if(pivot<animatedPivots)
            {
                var accumulated=new EnbCurrentRetailHierarchyAnimationAccumulator();
                for(int entry=0;entry<owner.Count;entry++)
                {
                    T? animation=owner.RetainedAnimation(entry);
                    if(animation is null)continue;
                    float frame=(float)owner.CurrentFrame(entry);
                    TMask? mask=owner.RetainedMask(entry);
                    float weight=(float)owner.Weight(entry);
                    if(mask is not null){weight=(float)((R)weight*owner.MaskWeight(mask,pivot));owner.ReleaseMask(mask);}
                    if(weight!=0&&!float.IsNaN(weight))
                    {
                        Vector3 offset=owner.Translation(animation,pivot,BitConverter.SingleToUInt32Bits(frame));
                        accumulated.AddTranslation(offset,weight,Word(header,6));
                        Quaternion sampled=owner.Rotation(animation,pivot,BitConverter.SingleToUInt32Bits(frame));
                        accumulated.AddRotation(sampled,weight,owner.Interpolate);
                    }
                    owner.ReleaseAnimation(animation);
                }
                WriteMatrix(record[0x48..],accumulated.Apply(ReadMatrix(record[0x48..])));
                record[0x78]=0;
                for(int entry=0;entry<owner.Count;entry++)
                {
                    if(record[0x78]!=0)break;
                    T? animation=owner.RetainedAnimation(entry);if(animation is null)continue;
                    float frame=(float)owner.CurrentFrame(entry);
                    byte value=owner.Visibility(animation,pivot,BitConverter.SingleToUInt32Bits(frame));
                    record[0x78]|=value;
                    owner.ReleaseAnimation(animation);
                }
            }
            if(record[0x79]!=0){ApplyOverride(record);record[0x78]=1;}
        }
    }
}
