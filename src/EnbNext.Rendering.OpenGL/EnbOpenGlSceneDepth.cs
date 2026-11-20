using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

/// <summary>
/// GPU storage encoding only: retain canonical clipping, projection and authored
/// comparisons while preserving useful depth precision throughout a sector.
/// All mesh lanes must use the same encoding and camera clip range.
/// </summary>
public static class EnbOpenGlSceneDepth
{
    public static Vector2 Parameters(EnbRenderClipRange clip)
    {
        if(!clip.IsValid)throw new ArgumentOutOfRangeException(nameof(clip));
        double near=Math.Log2(1d+clip.NearPlane),far=Math.Log2(1d+clip.FarPlane);
        return new((float)near,(float)(1d/(far-near)));
    }

    public static float Encode(EnbRenderClipRange clip,float viewDistance)
    {
        if(!float.IsFinite(viewDistance)||viewDistance<=0)throw new ArgumentOutOfRangeException(nameof(viewDistance));
        Vector2 parameters=Parameters(clip);
        return Math.Clamp((MathF.Log2(1f+viewDistance)-parameters.X)*parameters.Y,0,1);
    }

    public static Vector4 Uniforms(EnbRenderClipRange clip, Matrix4x4 clipFromModel)
    {
        Vector2 logarithmic = Parameters(clip);
        bool orthographic = clipFromModel.M14 == 0 && clipFromModel.M24 == 0 &&
            clipFromModel.M34 == 0 && clipFromModel.M44 == 1;
        return new(logarithmic, orthographic ? clip.NearPlane : 0,
            orthographic ? clip.FarPlane : 0);
    }

    public static string FragmentShader(string source,GlVersion version)
    {
        bool es=version.Type==GlProfileType.OpenGLES;
        bool legacyEs=es && version.Major<3;
        if(legacyEs)
        {
            // ES2 needs its explicit fragment-depth extension; do not silently
            // mix perspective depth with the other mesh lanes.
            int insert=source.StartsWith("#version",StringComparison.Ordinal)?source.IndexOf('\n')+1:0;
            source=source.Insert(insert,"#extension GL_EXT_frag_depth : require\n#extension GL_OES_standard_derivatives : require\n");
        }
        string precision=es?"highp ":"";
        return source.Replace("void main()","void shadeSceneFragment()",StringComparison.Ordinal)+
            $"\nuniform {precision}vec4 uSceneDepth;\nuniform {precision}vec4 uSceneDepthBias;\nvoid main(){{ shadeSceneFragment(); {precision}float d=1.0/gl_FragCoord.w; if(uSceneDepth.z>0.0) d=mix(uSceneDepth.z,uSceneDepth.w,(gl_FragCoord.z-gl_DepthRange.near)/gl_DepthRange.diff); "+
            // Bias the canonical device depth, then re-encode its view distance.
            // q omits the constant projection term, preserving slope precision.
            "if(uSceneDepthBias.y>0.0){ float q=uSceneDepth.z>0.0?d/uSceneDepthBias.w:-uSceneDepthBias.z/d; float slope=max(abs(dFdx(q)),abs(dFdy(q))); float b=uSceneDepthBias.x/gl_DepthRange.diff+uSceneDepthBias.y*slope; if(uSceneDepth.z>0.0)d=max(0.0,d-b*uSceneDepthBias.w); else d=d/(1.0+b*d/uSceneDepthBias.z); } "+
            $"{precision}float z=(log2(1.0+d)-uSceneDepth.x)*uSceneDepth.y; "+
            (legacyEs?"gl_FragDepthEXT":"gl_FragDepth")+"=clamp(gl_DepthRange.near+gl_DepthRange.diff*(uSceneDepthBias.y>0.0?z:clamp(z,0.0,1.0)),0.0,1.0); }\n";
    }
}
