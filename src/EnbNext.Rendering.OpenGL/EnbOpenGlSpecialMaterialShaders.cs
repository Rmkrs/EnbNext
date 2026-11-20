using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

public static class EnbOpenGlSpecialMaterialShaders
{
    public static (string Vertex, string Fragment) Create(GlVersion version, uint selector, int mode, int pass,
        bool modulateAlphaAddColor)
    {
        if (!(selector is 1 or 2 or 5 && mode == 0 && pass == 0 || selector == 3 && mode == 1 && pass == 0 ||
            selector == 4 && mode is >= 1 and <= 3 && pass >= 0 && pass < (mode == 1 ? 1 : 2)))
            throw new ArgumentOutOfRangeException(nameof(mode), "Unadmitted special-material program.");
        bool es = version.Type == GlProfileType.OpenGLES, modern = es ? version.Major >= 3 : version.Major >= 3;
        string header = es ? modern ? "#version 300 es\nprecision highp float;\n" : "precision highp float;\n" : modern ? "#version 150\n" : "#version 120\n";
        string input = modern ? "in" : "attribute", vsOut = modern ? "out" : "varying", psIn = modern ? "in" : "varying";
        string vsIo = $"{vsOut} vec4 oD0;\n{vsOut} vec4 oD1;\n{vsOut} vec4 oT0;\n{vsOut} vec4 oT1;\n";
        string psIo = $"{psIn} vec4 oD0;\n{psIn} vec4 oD1;\n{psIn} vec4 oT0;\n{psIn} vec4 oT1;\n";
        string attributes = $"{input} vec3 aPosition;\n{input} vec3 aNormal;\n{input} vec4 aColor1;\n{input} vec2 aTexCoord0;\n";
        string vertex;
        if (mode == 0)
        {
            string coordinate = selector == 5
                ? "vec3 eye=(uViewFromModel*vec4(aPosition,1.0)).xyz; float d=length(eye); vec3 e=d>0.0?-eye/d:vec3(0.0); vec3 normal=(uViewFromWorld*vec4(worldNormal,0.0)).xyz; oT0=vec4(2.0*dot(e,normal)*normal-e,1.0);"
                : "oT0=vec4(aTexCoord0,0.0,1.0);";
            string body = EnbOpenGlFixedFunctionLightingBackend.CreateLightingVertexBody()
                .Replace("vTexCoord0=aTexCoord0;", coordinate + "oT1=oT0;oD0=vDiffuse;oD1=vec4(vSpecular,0.0);");
            vertex = header + attributes + vsIo + "vec4 vDiffuse;vec3 vSpecular;float vFogVisibility;uniform mat4 uViewFromWorld;\n" +
                EnbOpenGlFixedFunctionLightingBackend.CreateLightingUniformDeclarations() + body;
        }
        else
        {
            string instructions = selector == 3 ? EnbOpenGlSpecialMaterialPrograms.P00bcd08c : mode switch
            {
                1 => EnbOpenGlSpecialMaterialPrograms.P00bcb8fc,
                2 => pass == 0 ? EnbOpenGlSpecialMaterialPrograms.P00bcbba0 : EnbOpenGlSpecialMaterialPrograms.P00bcbfec,
                _ => pass == 0 ? EnbOpenGlSpecialMaterialPrograms.P00bcc43c : EnbOpenGlSpecialMaterialPrograms.P00bcc9a0
            };
            attributes += $"{input} vec3 aAux0;\n{input} vec3 aAux1;\n{input} vec3 aAux2;\n";
            vertex = header + attributes + vsIo + "uniform vec4 c[34];\nvoid main(){\n" +
                "vec4 v0=vec4(aPosition,1.0),v1=vec4(aNormal,1.0),v2=aColor1,v3=vec4(aTexCoord0,0.0,1.0);\n" +
                "vec4 v4=vec4(aAux0,1.0),v5=vec4(aAux1,1.0),v6=vec4(aAux2,1.0);\n" +
                string.Join("", Enumerable.Range(0,12).Select(i=>$"vec4 r{i}=vec4(0.0);")) +
                "vec4 oPos=vec4(0.0);oD0=vec4(0.0);oD1=vec4(0.0);oT0=vec4(0.0);oT1=vec4(0.0);\n" + instructions +
                "\noD0=clamp(oD0,0.0,1.0);oD1=clamp(oD1,0.0,1.0);gl_Position=vec4(oPos.xy,2.0*oPos.z-oPos.w,oPos.w);}\n";
        }
        string sample = modern ? "texture" : selector == 5 ? "textureCube" : "texture2D";
        string sampler = selector == 5 ? "samplerCube" : "sampler2D";
        // A11B00 never enables projected texture coordinates. PS1.1 tex t0
        // consumes interpolated XY, despite the projected VS writing XYZW.
        string coordinate0 = selector == 5 ? "coordinate.xyz" : "coordinate.xy";
        string sampleFunctions = $"uniform {sampler} texture0;uniform sampler2D texture1;uniform int hasTexture0;uniform int hasTexture1;\n" +
            $"vec4 sample0(vec4 coordinate){{return hasTexture0!=0?{sample}(texture0,{coordinate0}):vec4(1.0);}}\n" +
            $"vec4 sample1(vec4 coordinate){{return hasTexture1!=0?{(modern ? "texture" : "texture2D")}(texture1,coordinate.xy):vec4(1.0);}}\n";
        string bodyPs;
        if (selector == 4 && mode == 3)
            bodyPs = "vec4 v0=oD0,v1=oD1,t0=vec4(0.0),t1=vec4(0.0),r0=vec4(0.0),r1=vec4(0.0);\n" +
                (pass == 0 ? EnbOpenGlSpecialMaterialPrograms.P00bcc8fc : EnbOpenGlSpecialMaterialPrograms.P00bcd050) + "\nvec4 color=r0;";
        else if (selector == 4 && mode == 2)
        {
            // The D3D8 offline receipt verifies DOT3 writes alpha too. The
            // following stage replaces RGB with texture*oD1; alpha uses
            // ADDSMOOTH on pass 0 and SELECTARG1 on pass 1.
            bodyPs = "vec4 t=sample0(oT0);float d=clamp(dot(t.rgb*2.0-1.0,oD0.rgb*2.0-1.0),0.0,1.0);" +
                "vec4 color=vec4(sample1(oT1).rgb*oD1.rgb," + (pass == 0 ? "d+oD0.a*(1.0-d)" : "d") + ");if(hasTexture1==0)color=vec4(d);if(hasTexture0==0)color=oD0;";
        }
        else
        {
            bool alphaSpecular = selector is 2 or 3 && modulateAlphaAddColor;
            string alpha = selector is 1 or 5 ? "t.a*oD0.a" : alphaSpecular ? "t.a" : "oD0.a";
            string rgb = "t.rgb*oD0.rgb" + (alphaSpecular ? "+t.a*oD1.rgb" : selector == 5 ? "+oD1.rgb" : "");
            bodyPs = $"vec4 t=sample0(oT0);vec4 color=vec4({rgb},{alpha});if(hasTexture0==0)color=oD0" +
                (selector==5?"+vec4(oD1.rgb,0.0);":";");
        }
        string fragment = header + psIo + sampleFunctions + (modern ? "out vec4 fragColor;\n" : "") +
            "void main(){" + bodyPs + (modern ? "fragColor" : "gl_FragColor") + "=clamp(color,0.0,1.0);}\n";
        vertex=vertex.Replace("void main()","void specialVertex()",StringComparison.Ordinal)+
            "\nuniform vec4 uRasterOffset;void main(){specialVertex();gl_Position.xy+=uRasterOffset.xy*gl_Position.w;}\n";
        return (vertex, fragment);
    }
}
