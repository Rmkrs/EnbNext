using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbOwnedLineScratchTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReusedBatchesAndDirtyScratchMatchDetachedArithmetic(bool frozen)
    {
        var subdivision=new EnbRetailLineSubdivision.Storage();var scratchStorage=new EnbRetailLineCoalescing.Storage();
        foreach(int depth in new[]{0,1,7})foreach(int count in new[]{10,2,7,3,10})
        {
            var points=Enumerable.Range(0,count).Select(i=>new EnbRetailLineSubdivision.Node(new(4+i*.17f,MathF.Sin(i)*.3f,-3-i*.25f),i){Color=new(.25f,.5f,.75f,1),V=i*.125f}).ToArray();
            var oracle=EnbRetailLineSubdivision.BuildBatches(points,depth,.125f,frozen,counter:17,precision:EnbRetailLinePrecision.Single24);
            EnbRetailLineSubdivision.BuildOwned(points,depth,.125f,frozen,17,EnbRetailLinePrecision.Single24,subdivision);
            Assert.Equal(oracle.Batches.Count,subdivision.Count);
            for(int i=0;i<subdivision.Count;i++)
            {
                var nodes=subdivision.Batches[i].Active;
                Assert.Equal(oracle.Batches[i].Nodes,nodes.ToArray());
                var expected=EnbRetailLineCoalescing.Construct(oracle.Batches[i].Nodes,.25f,default,EnbRetailLinePrecision.Single24);
                var actual=EnbRetailLineCoalescing.Construct(nodes,.25f,default,EnbRetailLinePrecision.Single24,scratchStorage);
                Assert.Equal(expected.Upper,actual.Upper.AsSpan(0,actual.Count).ToArray());
                Assert.Equal(expected.Lower,actual.Lower.AsSpan(0,actual.Count).ToArray());
                foreach(bool merge in new[]{false,true})
                {
                    var a=merge?EnbRetailLineCoalescing.Compact(expected.Upper,expected.Lower,expected.Planes,.25f,1,default,EnbRetailLinePrecision.Single24,false):new EnbRetailLineCoalescing.Result(expected.Upper,expected.Lower);
                    var b=merge?EnbRetailLineCoalescing.Compact(actual.Upper,actual.Lower,actual.Planes,.25f,1,default,EnbRetailLinePrecision.Single24,false,scratchStorage,actual.Count):new EnbRetailLineCoalescing.Result(actual.Upper,actual.Lower){UpperCount=actual.Count,LowerCount=actual.Count};
                    var wanted=EnbRetailLineCoalescing.Emit(oracle.Batches[i].Nodes,a,0,1,new(.125f,.25f),EnbRetailLinePrecision.Single24);
                    var got=EnbRetailLineCoalescing.Emit(nodes,b,0,1,new(.125f,.25f),EnbRetailLinePrecision.Single24,scratchStorage);
                    Assert.Equal(wanted.Vertices,got.Vertices.AsSpan(0,got.VertexCount).ToArray());
                    Assert.Equal(wanted.Indices,got.Indices.AsSpan(0,got.IndexCount).ToArray());
                }
            }
        }
    }
}
