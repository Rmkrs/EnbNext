using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

public sealed record EnbOpenGlSpecialMaterialSubmission(EnbW3dDrawTask Packet, EnbResolvedRenderCamera Camera);
public sealed record EnbOpenGlSpecialMaterialTextures(EnbOpenGlTextureStageUploadPlan? Base,
    EnbOpenGlTextureStageUploadPlan? Normal, EnbRetailCubeTexture? Cube);
public sealed record EnbOpenGlSpecialMaterialReceipt(int DrawCalls, int Primitives, IReadOnlyList<string> Diagnostics)
{ public int Vertices { get; init; } }

/// <summary>A08060/A08270: selector order, then pass order, FIFO delegates.
/// Geometry belongs to the cloned RenderObj; textures and projection belong to
/// the shared material wrapper. Device release preserves their CPU ownership.</summary>
public sealed class EnbOpenGlSpecialMaterialBackend
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void V4(int location,float x,float y,float z,float w);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Pair(int a,int b);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void One(int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Parameter(int target,int name,int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ParameterF(int target,int name,float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TextureImage(int target,int level,int format,int width,int height,int border,int pixelFormat,int type,IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void CompressedImage(int target,int level,int format,int width,int height,int border,int length,IntPtr data);
    private sealed class GeometryGpu { public int Vertex, Index, Array, Stride, Count; }
    private sealed class MaterialGpu
    {
        public EnbOpenGlSpecialMaterialTextures? Retained;
        public int Base, Normal, Cube;
    }
    private readonly Dictionary<(uint,int,int,bool),int> _programs=[];
    private readonly Dictionary<(uint,int,int,bool),string> _errors=[];
    private ConditionalWeakTable<EnbRetailSpecialMaterialGeometry,GeometryGpu> _geometry=new();
    private ConditionalWeakTable<EnbCurrentRetailSpecialMaterialResource,MaterialGpu> _materials=new();
    private readonly List<(WeakReference<object> Owner, GeometryGpu Gpu)> _geometryHandles=[];
    private readonly List<(WeakReference<object> Owner, MaterialGpu Gpu)> _materialHandles=[];
    private V4 _v4=null!; private Pair _blend=null!,_pixelStore=null!; private One _front=null!,_cull=null!,_disableAttribute=null!;
    private Parameter _parameter=null!; private ParameterF _parameterF=null!;
    private TextureImage _image=null!; private CompressedImage _compressed=null!;
    public EnbRetailSpecialMaterialCapabilities Capabilities { get; private set; }
    public bool IsInitialized { get; private set; }
    // 9270D0 resets addressing, but neither it nor 94C040 resets filtering.
    // A0B040 binds the raw cube without TextureClass::Apply sampler writes.
    public EnbW3dTextureSamplerSemantics Stage0Sampler {get;set;}=new(
        EnbRenderTextureAddressMode.Wrap,EnbRenderTextureAddressMode.Wrap,
        EnbRenderTextureMinMagFilter.Point,EnbRenderTextureMinMagFilter.Point,EnbRenderTextureMipFilter.None,1,false);

    public void Initialize(GlInterface gl)
    {
        if(IsInitialized)throw new InvalidOperationException("Special-material backend already initialized.");
        _v4=Proc<V4>(gl,"glUniform4f");_blend=Proc<Pair>(gl,"glBlendFunc");
        _front=Proc<One>(gl,"glFrontFace");_cull=Proc<One>(gl,"glCullFace");
        _disableAttribute=Proc<One>(gl,"glDisableVertexAttribArray");_pixelStore=Proc<Pair>(gl,"glPixelStorei");
        _parameter=Proc<Parameter>(gl,"glTexParameteri");_parameterF=Proc<ParameterF>(gl,"glTexParameterf");
        _image=Proc<TextureImage>(gl,"glTexImage2D");_compressed=Proc<CompressedImage>(gl,"glCompressedTexImage2D");
        foreach(uint selector in new uint[]{1,2,3,4,5})
        foreach(int mode in selector==4?new[]{1,2,3}:new[]{selector==3?1:0})
        for(int pass=0;pass<(selector==4 && mode>1?2:1);pass++)
        foreach(bool fancy in selector is 2 or 3?new[]{false,true}:new[]{false})
        {
            var key=(selector,mode,pass,fancy);
            try { _programs.Add(key,Compile(gl,selector,mode,pass,fancy)); }
            catch(InvalidOperationException ex) { _errors.Add(key,ex.Message); }
        }
        // Declare the emulated native capabilities only after actual driver
        // compilation/linking, including both programs of each two-pass mode.
        bool ps=_programs.ContainsKey((4,3,0,false)) && _programs.ContainsKey((4,3,1,false));
        bool dot=_programs.ContainsKey((4,2,0,false)) && _programs.ContainsKey((4,2,1,false));
        bool fancySupported=_programs.ContainsKey((2,0,0,true)) && _programs.ContainsKey((3,1,0,true));
        Capabilities=new(ps?0x101u:0,dot,fancySupported); IsInitialized=true;
    }

    private static int Compile(GlInterface gl,uint selector,int mode,int pass,bool fancy)
    {
        using var programWork=EnbHitchEvents.Measure("program.compile.special");
        var sources=EnbOpenGlSpecialMaterialShaders.Create(gl.ContextInfo.Version,selector,mode,pass,fancy);
        int vs=gl.CreateShader(GlConsts.GL_VERTEX_SHADER), ps=gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER), program=0;
        try
        {
            string? error=gl.CompileShaderAndGetError(vs,sources.Vertex);
            if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException($"G07 {selector}/{mode}/{pass} VS: {error}");
            error=gl.CompileShaderAndGetError(ps,EnbOpenGlSceneDepth.FragmentShader(sources.Fragment,gl.ContextInfo.Version));
            if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException($"G07 {selector}/{mode}/{pass} PS: {error}");
            program=gl.CreateProgram();gl.AttachShader(program,vs);gl.AttachShader(program,ps);
            string[] names=["aPosition","aNormal","aColor1","aTexCoord0","aAux0","aAux1","aAux2"];
            for(int i=0;i<names.Length;i++)gl.BindAttribLocationString(program,i,names[i]);
            error=gl.LinkProgramAndGetError(program);
            if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException($"G07 {selector}/{mode}/{pass} link: {error}");
            return program;
        }
        catch {if(program!=0)gl.DeleteProgram(program);throw;}
        finally {gl.DeleteShader(vs);gl.DeleteShader(ps);}
    }

    public EnbOpenGlSpecialMaterialReceipt Submit(GlInterface gl,int framebuffer,int width,int height,
        IReadOnlyList<EnbOpenGlSpecialMaterialSubmission> submissions,EnbW3dLightEnvironment globalLights,
        Vector3 globalAmbient,Func<EnbCurrentRetailSpecialMaterialResource,EnbOpenGlSpecialMaterialTextures> textures,
        EnbRetailSpecialMaterialCapabilities? capabilityOverride=null, int viewportInset=0, bool retailCapturePixelCenters=false, Action<EnbW3dDrawTask>? beforeDraw=null)
    {
        if(!IsInitialized)throw new InvalidOperationException("Special-material backend is not initialized.");
        var caps=capabilityOverride??Capabilities;
        int draws=0,primitives=0,vertices=0;var errors=new List<string>();
        var constants=new EnbRetailSpecialMaterialConstants();
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER,framebuffer);gl.Viewport(viewportInset,viewportInset,width-2*viewportInset,height-2*viewportInset);
        gl.Enable(0x0b71);gl.DepthMask(1);gl.DepthFunc(0x0203);gl.Enable(0x0b44);
        _front(0x0901);_cull(0x0405);gl.Disable(0x0be2);
        foreach(var family in submissions.GroupBy(s=>s.Packet.SpecialMaterial!.Resource.Selector).OrderBy(g=>g.Key))
        {
            uint selector=family.Key;int mode=caps.Mode(selector);
            var queue=family.ToArray();var first=queue[0].Packet.SpecialMaterial!.Resource;
            for(int pass=0;pass<(selector==4 && mode>1?2:1);pass++)
            {
                bool fancy=selector is 2 or 3 && caps.ModulateAlphaAddColor;
                var key=(selector,mode,pass,fancy);
                if(!_programs.TryGetValue(key,out int program)) {errors.Add(_errors.GetValueOrDefault(key,"Unadmitted special-material program."));continue;}
                gl.UseProgram(program);
                var raster=retailCapturePixelCenters?EnbOpenGlRenderTexture.RetailCaptureRasterTranslation(width-2*viewportInset,height-2*viewportInset):Matrix4x4.Identity;
                Vector(gl,program,"uRasterOffset",new(raster.M41,raster.M42,0,0));
                if(selector==4 && mode==2) {gl.Enable(0x0be2);_blend(0x0302,pass==0?0:1);}
                // Selector5 intentionally inherits selector4 mode2's blend.
                if(mode==0)FixedLighting(gl,program,first,globalLights,globalAmbient);
                foreach(var submission in queue)
                {
                    var packet=submission.Packet;var draw=packet.SpecialMaterial!;
                    if(!draw.Instance.AdmitsPass(selector,mode,pass))continue;
                    try
                    {
                        var material=_materials.GetValue(draw.Resource,r=>
                        {var value=new MaterialGpu();_materialHandles.Add((new(r),value));return value;});
                        material.Retained??=textures(draw.Resource);
                        EnsureTextures(gl,material);
                        var gpu=GetGeometry(gl,draw.Geometry,selector,mode);
                        var projectionState=EnbRetailSpecialMaterialProjectionState.For(draw.Resource);
                        if(selector==4 && mode==3 && pass==1)
                        {
                            if(draw.Instance.RetainedProjection is EnbRetailSpecialMaterialProjection projection)
                                projectionState.Matrix=projection.Create(packet.WorldTransform,submission.Camera);
                            if(projectionState.Matrix is null)
                                throw new InvalidDataException("Projected special-material member has no initialized projector matrix.");
                        }
                        constants.SetDraw(packet,submission.Camera,globalLights,globalAmbient,mode,projectionState.Matrix??default);
                        EnbRenderCameraMatrixCompiler.TryCreateCanonicalClipFromModel(submission.Camera,packet.WorldTransform,out var clip);
                        Vector(gl,program,"uSceneDepth",EnbOpenGlSceneDepth.Uniforms(submission.Camera.Projection.ClipRange,clip));
                        for(int i=0;i<34;i++)Vector(gl,program,$"c[{i}]",constants.Registers[i]);
                        if(mode==0)FixedMatrices(gl,program,packet,submission.Camera);
                        int baseId=material.Base,normalId=material.Normal;
                        bool normalFirst=selector==4 && mode>1 && pass==0 || selector==4 && mode==2;
                        int t0=selector==5?material.Cube:normalFirst?normalId:baseId;
                        int t1=normalFirst?baseId:0;
                        gl.ActiveTexture(0x84c0);gl.BindTexture(selector==5?0x8513:0x0de1,t0);
                        if(selector==5 && t0!=0)
                        {
                            _parameter(0x8513,0x2801,EnbOpenGlStage0TextureUploadPlanner.MapMinFilter(Stage0Sampler,material.Retained!.Cube!.Faces[0].MipLevels.Count));
                            _parameter(0x8513,0x2800,Stage0Sampler.MagFilter==EnbRenderTextureMinMagFilter.Point?0x2600:0x2601);
                            if(EnbOpenGlStage0TextureBackend.SupportsAnisotropicFiltering(gl.ContextInfo.Extensions))_parameterF(0x8513,0x84fe,Stage0Sampler.MaximumAnisotropy);
                        }
                        else if(selector!=5 && (normalFirst?material.Retained!.Normal:material.Retained!.Base) is {} applied)
                            Stage0Sampler=applied.Sampler;
                        gl.ActiveTexture(0x84c1);gl.BindTexture(0x0de1,t1);
                        gl.Uniform1i(gl.GetUniformLocationString(program,"texture0"),0);
                        gl.Uniform1i(gl.GetUniformLocationString(program,"texture1"),1);
                        gl.Uniform1i(gl.GetUniformLocationString(program,"hasTexture0"),t0!=0?1:0);
                        gl.Uniform1i(gl.GetUniformLocationString(program,"hasTexture1"),t1!=0?1:0);
                        if(gl.IsBindVertexArrayAvailable)gl.BindVertexArray(gpu.Array);
                        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,gpu.Vertex);gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER,gpu.Index);
                        Layout(gl,gpu.Stride);
                        beforeDraw?.Invoke(packet);
                        gl.DrawElements(GlConsts.GL_TRIANGLES,gpu.Count,GlConsts.GL_UNSIGNED_SHORT,IntPtr.Zero);
                        draws++;primitives+=gpu.Count/3;vertices+=draw.Geometry.Positions.Length;
                    }
                    catch(Exception ex) when(ex is InvalidDataException or ArgumentException or InvalidOperationException or IOException)
                    {errors.Add($"{packet.RenderObjectName}/{draw.MemberOrdinal}: {ex.Message}");}
                }
            }
        }
        if(gl.IsBindVertexArrayAvailable)gl.BindVertexArray(0);
        gl.ActiveTexture(0x84c0);gl.Disable(0x0be2);
        Collect(gl);
        return new(draws,primitives,errors) { Vertices=vertices };
    }

    private GeometryGpu GetGeometry(GlInterface gl,EnbRetailSpecialMaterialGeometry geometry,uint selector,int mode)
    {
        var gpu=_geometry.GetValue(geometry,g=>{var value=new GeometryGpu();_geometryHandles.Add((new(g),value));return value;});
        int stride=selector==5?28:selector==4 && mode>1?72:36;
        if(gpu.Vertex!=0 && gpu.Stride==stride)return gpu;
        DeleteGeometry(gl,gpu);gpu.Stride=stride;gpu.Count=geometry.Indices.Length;
        if(gl.IsGenVertexArraysAvailable){gpu.Array=gl.GenVertexArray();gl.BindVertexArray(gpu.Array);}
        gpu.Vertex=gl.GenBuffer();gpu.Index=gl.GenBuffer();
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,gpu.Vertex);Buffer(gl,GlConsts.GL_ARRAY_BUFFER,geometry.Fill(selector,mode).AsSpan());
        gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER,gpu.Index);Buffer(gl,GlConsts.GL_ELEMENT_ARRAY_BUFFER,MemoryMarshal.AsBytes(geometry.Indices.AsSpan()));
        Layout(gl,stride);return gpu;
    }
    private void Layout(GlInterface gl,int stride)
    {
        Attribute(0,3,0);Attribute(1,3,12);
        gl.VertexAttribPointer(2,4,GlConsts.GL_UNSIGNED_BYTE,1,stride,new(24));gl.EnableVertexAttribArray(2);
        if(stride>28)Attribute(3,2,28);else _disableAttribute(3);
        for(int i=4;i<7;i++)if(stride==72)Attribute(i,3,36+(i-4)*12);else _disableAttribute(i);
        void Attribute(int id,int count,int at){gl.VertexAttribPointer(id,count,GlConsts.GL_FLOAT,0,stride,new(at));gl.EnableVertexAttribArray(id);}
    }
    private static unsafe void Buffer(GlInterface gl,int target,ReadOnlySpan<byte> bytes)
    {fixed(byte* p=bytes)gl.BufferData(target,new(bytes.Length),new(p),0x88e4);}

    private void FixedMatrices(GlInterface gl,int program,EnbW3dDrawTask packet,EnbResolvedRenderCamera camera)
    {
        EnbRenderCameraMatrixCompiler.TryCreateCanonicalClipFromModel(camera,packet.WorldTransform,out var clip);
        var cameraView=EnbW3dGeneratedTextureCoordinates.CreateRetailCameraViewFromWorld(camera);
        var view=packet.WorldTransform*cameraView;
        Matrix4x4.Invert(packet.WorldTransform,out var inverse);
        Matrix(gl,program,"uClipFromModel",EnbOpenGlFirstW3dShaderBackend.CreateOpenGlClipFromCanonical(clip));
        Matrix(gl,program,"uViewFromModel",view);Matrix(gl,program,"uViewFromWorld",cameraView);
        Matrix(gl,program,"uWorldFromModel",packet.WorldTransform);Matrix(gl,program,"uNormalFromModel",Matrix4x4.Transpose(inverse));
        Vector(gl,program,"uCameraWorldPosition",new(camera.Position,0));
    }
    private void FixedLighting(GlInterface gl,int program,EnbCurrentRetailSpecialMaterialResource resource,EnbW3dLightEnvironment lights,Vector3 ambient)
    {
        Vector(gl,program,"uGlobalAmbient",new(Rgb(lights.GlobalAmbientRgb24),0));
        Vector(gl,program,"uMaterialAmbient",new(resource.Ambient,0));Vector(gl,program,"uMaterialDiffuse",new(resource.Diffuse,0));
        Vector(gl,program,"uMaterialSpecular",new(resource.Specular,0));Vector(gl,program,"uMaterialEmissive",Vector4.Zero);
        Vector(gl,program,"uMaterialSources",Vector4.Zero);Vector(gl,program,"uMaterialPowerSpecular",new(resource.Selector is 2 or 5?20:0,resource.Selector==5?1:0,0,0));
        for(int i=0;i<4;i++)
        {
            var slot=lights.Lights[i];var light=slot.Light;string prefix=$"uLight{i}";
            Vector(gl,program,prefix+"State",new(slot.Enabled?1:0,(float)light.Type,light.Range,light.Falloff));
            Vector(gl,program,prefix+"Diffuse",new(light.Diffuse,0));
            Vector(gl,program,prefix+"Specular",new(i==0?Vector3.One:light.Specular,0));
            Vector(gl,program,prefix+"Ambient",new(i==0?ambient:light.Ambient,0));
            Vector(gl,program,prefix+"PositionPhi",new(light.Position,light.Phi));
            Vector(gl,program,prefix+"DirectionTheta",new(light.Direction,light.Theta));
            Vector(gl,program,prefix+"Attenuation",new(light.Attenuation0,light.Attenuation1,light.Attenuation2,0));
        }
    }
    private void Vector(GlInterface gl,int program,string name,Vector4 v)
    {int at=gl.GetUniformLocationString(program,name);if(at>=0)_v4(at,v.X,v.Y,v.Z,v.W);}
    private static unsafe void Matrix(GlInterface gl,int program,string name,Matrix4x4 matrix)
    {
        int at=gl.GetUniformLocationString(program,name);if(at<0)return;
        var values=EnbOpenGlIndexedSubmissionBackend.CreateGlUniformMatrixElements(matrix).ToArray();
        fixed(float* p=values)gl.UniformMatrix4fv(at,1,false,p);
    }
    private void EnsureTextures(GlInterface gl,MaterialGpu material)
    {
        if(material.Base==0 && material.Retained!.Base is {} b)material.Base=Upload(gl,b);
        if(material.Normal==0 && material.Retained!.Normal is {} n)material.Normal=Upload(gl,n);
        if(material.Cube==0 && material.Retained!.Cube is {} cube)material.Cube=UploadCube(gl,cube);
    }
    private int Upload(GlInterface gl,EnbOpenGlTextureStageUploadPlan plan)
    {
        int texture=gl.GenTexture();gl.ActiveTexture(0x84c0);gl.BindTexture(0x0de1,texture);
        try
        {
            _parameter(0x0de1,0x2802,plan.GlWrapS);_parameter(0x0de1,0x2803,plan.GlWrapT);
            _parameter(0x0de1,0x2801,plan.GlMinFilter);_parameter(0x0de1,0x2800,plan.GlMagFilter);
            if(EnbOpenGlStage0TextureBackend.SupportsTextureMaximumLevel(gl.ContextInfo.Version))_parameter(0x0de1,0x813d,plan.MaximumMipLevel);
            if(plan.MaximumAnisotropy>1)_parameterF(0x0de1,0x84fe,plan.MaximumAnisotropy);
            foreach(var mip in plan.Mips)UploadMip(gl,0x0de1,mip);
            return texture;
        }
        catch {gl.DeleteTexture(texture);throw;}
    }
    private unsafe void UploadMip(GlInterface gl,int target,EnbOpenGlStage0TextureMipUpload mip)
    {
        using var hitch=EnbHitchEvents.Measure("texture.upload.special");
        if(!mip.CompressedBlocks.IsEmpty)
        {fixed(byte* p=mip.CompressedBlocks.Span)_compressed(target,mip.Level,mip.GlInternalFormat,mip.Width,mip.Height,0,mip.CompressedBlocks.Length,new(p));return;}
        bool packed=!mip.PackedBytes.IsDefaultOrEmpty;
        _pixelStore(0x0cf5,1);
        fixed(byte* p=(packed?mip.PackedBytes.AsSpan():mip.RgbaBytes))_image(target,mip.Level,mip.GlInternalFormat,mip.Width,mip.Height,0,mip.GlPixelFormat,mip.GlPixelType,new(p));
        _pixelStore(0x0cf5,4);
    }
    private unsafe int UploadCube(GlInterface gl,EnbRetailCubeTexture cube)
    {
        int texture=gl.GenTexture();gl.ActiveTexture(0x84c0);gl.BindTexture(0x8513,texture);
        try
        {
            _parameter(0x8513,0x2802,0x812f);_parameter(0x8513,0x2803,0x812f);_parameter(0x8513,0x8072,0x812f);
            _parameter(0x8513,0x2801,cube.Faces[0].MipLevels.Count>1?0x2701:0x2601);_parameter(0x8513,0x2800,0x2601);
            if(EnbOpenGlStage0TextureBackend.SupportsTextureMaximumLevel(gl.ContextInfo.Version))_parameter(0x8513,0x813d,cube.Faces[0].MipLevels.Count-1);
            for(int face=0;face<6;face++)
            {
                var image=cube.Faces[face];
                var binding=new EnbW3dRenderTextureBinding(0,"cube-face",null);
                var mipPlan=new EnbTextureUploadMipPlan(0,image.MipLevels.Count,image.Width,image.Height);
                var sampler=EnbW3dTextureSamplerCompiler.Compile(binding,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault);
                if(!EnbOpenGlStage0TextureUploadPlanner.TryPlanStage(0,binding,image,mipPlan,sampler,out var plan,out var error))
                    throw new InvalidDataException(error);
                foreach(var mip in plan.Mips)UploadMip(gl,0x8515+face,mip);
            }
            return texture;
        }
        catch {gl.DeleteTexture(texture);throw;}
    }
    public void Deinitialize(GlInterface? gl,bool preserveScene=true)
    {
        foreach(var item in _geometryHandles)DeleteGeometry(gl,item.Gpu);
        foreach(var item in _materialHandles)DeleteMaterial(gl,item.Gpu);
        foreach(int program in _programs.Values)gl?.DeleteProgram(program);
        _programs.Clear();_errors.Clear();IsInitialized=false;
        if(!preserveScene) {_geometry=new();_materials=new();_geometryHandles.Clear();_materialHandles.Clear();}
    }
    public void ResetScene(GlInterface? gl)
    {
        foreach(var item in _geometryHandles)DeleteGeometry(gl,item.Gpu);
        foreach(var item in _materialHandles){DeleteMaterial(gl,item.Gpu);item.Gpu.Retained=null;}
        Collect(gl);
    }
    private void Collect(GlInterface? gl)
    {
        _geometryHandles.RemoveAll(item=>{if(item.Owner.TryGetTarget(out _))return false;DeleteGeometry(gl,item.Gpu);return true;});
        _materialHandles.RemoveAll(item=>{if(item.Owner.TryGetTarget(out _))return false;DeleteMaterial(gl,item.Gpu);return true;});
    }
    private static void DeleteGeometry(GlInterface? gl,GeometryGpu gpu)
    {if(gpu.Vertex!=0)gl?.DeleteBuffer(gpu.Vertex);if(gpu.Index!=0)gl?.DeleteBuffer(gpu.Index);if(gpu.Array!=0)gl?.DeleteVertexArray(gpu.Array);gpu.Vertex=gpu.Index=gpu.Array=0;}
    private static void DeleteMaterial(GlInterface? gl,MaterialGpu gpu)
    {if(gpu.Base!=0)gl?.DeleteTexture(gpu.Base);if(gpu.Normal!=0)gl?.DeleteTexture(gpu.Normal);if(gpu.Cube!=0)gl?.DeleteTexture(gpu.Cube);gpu.Base=gpu.Normal=gpu.Cube=0;}
    private static T Proc<T>(GlInterface gl,string name) where T:Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(gl.GetProcAddress(name));
    private static Vector3 Rgb(uint v)=>new((v>>16&255)/255f,(v>>8&255)/255f,(v&255)/255f);
}
