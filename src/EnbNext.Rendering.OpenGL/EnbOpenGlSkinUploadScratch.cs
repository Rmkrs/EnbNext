using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Rendering.OpenGL;
// One resource owns this synchronous upload scratch. It never backs an immutable
// lane/diagnostic snapshot and contains no GL names or mapper/animation clocks.
internal sealed class EnbOpenGlSkinUploadScratch
{
    private float[] _elements=[];
    private EnbW3dColorStream _colors;
    private EnbW3dUvStream _uv;
    private bool _valid;
    internal int StaticRebuilds {get;private set;}
    internal ReadOnlySpan<float> Prepare(EnbOpenGlLitShaderUploadPlan geometry)
    {
        int count=checked(geometry.VertexCount*EnbOpenGlLitShaderUploadPlan.FloatElementsPerVertex);
        bool same=_valid && geometry.CanRetainStaticVertexSource && _elements.Length==count && _colors.SameImmutableSource(geometry.VertexColors) && _uv.SameImmutableSource(geometry.VertexUv);
        if(_elements.Length!=count)_elements=new float[count];
        if(same)geometry.CopyPoseElementsTo(_elements);
        else {geometry.CopyVertexElementsTo(_elements);StaticRebuilds++;}
        _colors=geometry.VertexColors;_uv=geometry.VertexUv;_valid=geometry.CanRetainStaticVertexSource;
        return _elements;
    }
}
