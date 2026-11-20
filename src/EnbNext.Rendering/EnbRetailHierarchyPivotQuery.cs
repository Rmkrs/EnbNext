using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>00989490/00989370 pivot matrix queries at retail PC24. The
/// output starts as identity, and the root pivot's local transform is skipped.</summary>
public static class EnbRetailHierarchyPivotQuery
{
    /// <summary>Authored locals and sampled matrices remain distinct from
    /// resolved pose-record overrides.</summary>
    public static Matrix4x4 Query(IReadOnlyList<Matrix4x4> authoredLocals,IReadOnlyList<int> parents,
        int pivot,Matrix4x4 rootMatrix,float translationScale=1f,Func<int,Matrix4x4>? sampleMatrix=null)
    {
        if(authoredLocals.Count!=parents.Count)throw new ArgumentException("One parent is required per authored pivot.");
        Span<float> destination=stackalloc float[12],identity=stackalloc float[12],local=stackalloc float[12],
            sample=stackalloc float[12],matrix=stackalloc float[12],root=stackalloc float[12];
        Identity(destination);
        if(pivot<0||pivot>=authoredLocals.Count)return EnbRetailAffineRows.Matrix(destination);
        Identity(identity);
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        for(int record=pivot;record>=0&&parents[record]>=0;record=parents[record])
        {
            EnbRetailAffineRows.Rows(authoredLocals[record],local);
            if(sampleMatrix is null)EnbRetailAffineRows.Multiply(local,identity,matrix);
            else
            {
                EnbRetailAffineRows.Rows(sampleMatrix(record),sample);
                R scale=translationScale;
                R x=(R)sample[3]*scale;
                float y=(float)((R)sample[7]*scale);
                float z=(float)((R)sample[11]*scale);
                sample[7]=y;sample[11]=z;sample[3]=(float)x;
                EnbRetailAffineRows.Multiply(local,sample,matrix);
            }
            EnbRetailAffineRows.Multiply(matrix,destination,destination);
        }
        EnbRetailAffineRows.Rows(rootMatrix,root);
        EnbRetailAffineRows.Multiply(root,destination,destination);
        return EnbRetailAffineRows.Matrix(destination);
    }
    private static void Identity(Span<float> rows){rows.Clear();rows[0]=rows[5]=rows[10]=1;}
}
