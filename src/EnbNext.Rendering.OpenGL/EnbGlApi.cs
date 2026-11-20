using System.Runtime.InteropServices;
using System.Text;

namespace EnbNext.Rendering.OpenGL;

public enum EnbGlProfileType { OpenGL, OpenGLES }
public readonly record struct EnbGlVersion
{
    public EnbGlProfileType Type { get; }
    public int Major { get; }
    public int Minor { get; }
    public bool IsCompatibilityProfile { get; }
    public EnbGlVersion(EnbGlProfileType type,int major,int minor,bool isCompatibilityProfile=false)
        => (Type,Major,Minor,IsCompatibilityProfile)=(type,major,minor,isCompatibilityProfile);
}

/// <summary>Context-owned public GL procedure table. No window, scene ownership or scheduler.</summary>
public sealed unsafe class EnbGlApi
{
    internal EnbOpenGlFrameQueue.Queue? FrameQueue;
    internal int SupersamplingFramebuffer;
    private int drawFramebuffer;
    public bool SceneAnisotropicFiltering { get; set; }
    private int anisotropyLimit;
    public int MaximumSupportedAnisotropy
    {
        get
        {
            if(anisotropyLimit==0)
            {
                anisotropyLimit=1;
                if(EnbOpenGlStage0TextureBackend.SupportsAnisotropicFiltering(ContextInfo.Extensions))
                {GetIntegerv(0x84FF,out int limit);anisotropyLimit=Math.Max(1,limit);}
            }
            return anisotropyLimit;
        }
    }
    public bool HighDynamicRange { get; set; }
    public sealed record GlContextInfo(EnbGlVersion Version,HashSet<string> Extensions);
    private readonly Func<string,nint> resolver;
    public GlContextInfo ContextInfo { get; }
    public string? Version { get; }
    public string? Renderer { get; }
    public string? Vendor { get; }
    public nint GetProcAddress(string name)=>resolver(name);
    public EnbGlApi(EnbGlVersion version,Func<string,nint> getProcAddress)
    {
        resolver=getProcAddress;
        pClearColor=(delegate* unmanaged<float,float,float,float,void>)getProcAddress("glClearColor");
        if(pClearColor==null)throw new NotSupportedException("Missing public GL entry point glClearColor.");
        pClearDepthDouble=(delegate* unmanaged<double,void>)getProcAddress("glClearDepth");
        pClearDepthFloat=(delegate* unmanaged<float,void>)getProcAddress("glClearDepthf");
        pDepthFunc=(delegate* unmanaged<int,void>)getProcAddress("glDepthFunc");
        if(pDepthFunc==null)throw new NotSupportedException("Missing public GL entry point glDepthFunc.");
        pDepthMask=(delegate* unmanaged<byte,void>)getProcAddress("glDepthMask");
        if(pDepthMask==null)throw new NotSupportedException("Missing public GL entry point glDepthMask.");
        pClear=(delegate* unmanaged<int,void>)getProcAddress("glClear");
        if(pClear==null)throw new NotSupportedException("Missing public GL entry point glClear.");
        pViewport=(delegate* unmanaged<int,int,int,int,void>)getProcAddress("glViewport");
        if(pViewport==null)throw new NotSupportedException("Missing public GL entry point glViewport.");
        pFinish=(delegate* unmanaged<void>)getProcAddress("glFinish");
        if(pFinish==null)throw new NotSupportedException("Missing public GL entry point glFinish.");
        pBindFramebuffer=(delegate* unmanaged<int,int,void>)getProcAddress("glBindFramebuffer");
        if(pBindFramebuffer==null)throw new NotSupportedException("Missing public GL entry point glBindFramebuffer.");
        pCheckFramebufferStatus=(delegate* unmanaged<int,int>)getProcAddress("glCheckFramebufferStatus");
        if(pCheckFramebufferStatus==null)throw new NotSupportedException("Missing public GL entry point glCheckFramebufferStatus.");
        pGenTextures=(delegate* unmanaged<int,int*,void>)getProcAddress("glGenTextures");
        if(pGenTextures==null)throw new NotSupportedException("Missing public GL entry point glGenTextures.");
        pBindTexture=(delegate* unmanaged<int,int,void>)getProcAddress("glBindTexture");
        if(pBindTexture==null)throw new NotSupportedException("Missing public GL entry point glBindTexture.");
        pActiveTexture=(delegate* unmanaged<int,void>)getProcAddress("glActiveTexture");
        if(pActiveTexture==null)throw new NotSupportedException("Missing public GL entry point glActiveTexture.");
        pDeleteTextures=(delegate* unmanaged<int,int*,void>)getProcAddress("glDeleteTextures");
        if(pDeleteTextures==null)throw new NotSupportedException("Missing public GL entry point glDeleteTextures.");
        pCreateShader=(delegate* unmanaged<int,int>)getProcAddress("glCreateShader");
        if(pCreateShader==null)throw new NotSupportedException("Missing public GL entry point glCreateShader.");
        pShaderSource=(delegate* unmanaged<int,int,IntPtr,IntPtr,void>)getProcAddress("glShaderSource");
        if(pShaderSource==null)throw new NotSupportedException("Missing public GL entry point glShaderSource.");
        pCompileShader=(delegate* unmanaged<int,void>)getProcAddress("glCompileShader");
        if(pCompileShader==null)throw new NotSupportedException("Missing public GL entry point glCompileShader.");
        pGetShaderiv=(delegate* unmanaged<int,int,int*,void>)getProcAddress("glGetShaderiv");
        if(pGetShaderiv==null)throw new NotSupportedException("Missing public GL entry point glGetShaderiv.");
        pGetShaderInfoLog=(delegate* unmanaged<int,int,int*,void*,void>)getProcAddress("glGetShaderInfoLog");
        if(pGetShaderInfoLog==null)throw new NotSupportedException("Missing public GL entry point glGetShaderInfoLog.");
        pCreateProgram=(delegate* unmanaged<int>)getProcAddress("glCreateProgram");
        if(pCreateProgram==null)throw new NotSupportedException("Missing public GL entry point glCreateProgram.");
        pAttachShader=(delegate* unmanaged<int,int,void>)getProcAddress("glAttachShader");
        if(pAttachShader==null)throw new NotSupportedException("Missing public GL entry point glAttachShader.");
        pLinkProgram=(delegate* unmanaged<int,void>)getProcAddress("glLinkProgram");
        if(pLinkProgram==null)throw new NotSupportedException("Missing public GL entry point glLinkProgram.");
        pGetProgramiv=(delegate* unmanaged<int,int,int*,void>)getProcAddress("glGetProgramiv");
        if(pGetProgramiv==null)throw new NotSupportedException("Missing public GL entry point glGetProgramiv.");
        pGetProgramInfoLog=(delegate* unmanaged<int,int,int*,void*,void>)getProcAddress("glGetProgramInfoLog");
        if(pGetProgramInfoLog==null)throw new NotSupportedException("Missing public GL entry point glGetProgramInfoLog.");
        pBindAttribLocation=(delegate* unmanaged<int,int,IntPtr,void>)getProcAddress("glBindAttribLocation");
        if(pBindAttribLocation==null)throw new NotSupportedException("Missing public GL entry point glBindAttribLocation.");
        pGenBuffers=(delegate* unmanaged<int,int*,void>)getProcAddress("glGenBuffers");
        if(pGenBuffers==null)throw new NotSupportedException("Missing public GL entry point glGenBuffers.");
        pBindBuffer=(delegate* unmanaged<int,int,void>)getProcAddress("glBindBuffer");
        if(pBindBuffer==null)throw new NotSupportedException("Missing public GL entry point glBindBuffer.");
        pBufferData=(delegate* unmanaged<int,IntPtr,IntPtr,int,void>)getProcAddress("glBufferData");
        if(pBufferData==null)throw new NotSupportedException("Missing public GL entry point glBufferData.");
        pVertexAttribPointer=(delegate* unmanaged<int,int,int,byte,int,IntPtr,void>)getProcAddress("glVertexAttribPointer");
        if(pVertexAttribPointer==null)throw new NotSupportedException("Missing public GL entry point glVertexAttribPointer.");
        pEnableVertexAttribArray=(delegate* unmanaged<int,void>)getProcAddress("glEnableVertexAttribArray");
        if(pEnableVertexAttribArray==null)throw new NotSupportedException("Missing public GL entry point glEnableVertexAttribArray.");
        pUseProgram=(delegate* unmanaged<int,void>)getProcAddress("glUseProgram");
        if(pUseProgram==null)throw new NotSupportedException("Missing public GL entry point glUseProgram.");
        pDrawArrays=(delegate* unmanaged<int,int,IntPtr,void>)getProcAddress("glDrawArrays");
        if(pDrawArrays==null)throw new NotSupportedException("Missing public GL entry point glDrawArrays.");
        pDrawElements=(delegate* unmanaged<int,int,int,IntPtr,void>)getProcAddress("glDrawElements");
        if(pDrawElements==null)throw new NotSupportedException("Missing public GL entry point glDrawElements.");
        pGetUniformLocation=(delegate* unmanaged<int,IntPtr,int>)getProcAddress("glGetUniformLocation");
        if(pGetUniformLocation==null)throw new NotSupportedException("Missing public GL entry point glGetUniformLocation.");
        pUniform1f=(delegate* unmanaged<int,float,void>)getProcAddress("glUniform1f");
        if(pUniform1f==null)throw new NotSupportedException("Missing public GL entry point glUniform1f.");
        pUniform1i=(delegate* unmanaged<int,int,void>)getProcAddress("glUniform1i");
        if(pUniform1i==null)throw new NotSupportedException("Missing public GL entry point glUniform1i.");
        pUniformMatrix4fv=(delegate* unmanaged<int,int,byte,void*,void>)getProcAddress("glUniformMatrix4fv");
        if(pUniformMatrix4fv==null)throw new NotSupportedException("Missing public GL entry point glUniformMatrix4fv.");
        pEnable=(delegate* unmanaged<int,void>)getProcAddress("glEnable");
        if(pEnable==null)throw new NotSupportedException("Missing public GL entry point glEnable.");
        pDisable=(delegate* unmanaged<int,void>)getProcAddress("glDisable");
        if(pDisable==null)throw new NotSupportedException("Missing public GL entry point glDisable.");
        pDeleteBuffers=(delegate* unmanaged<int,int*,void>)getProcAddress("glDeleteBuffers");
        if(pDeleteBuffers==null)throw new NotSupportedException("Missing public GL entry point glDeleteBuffers.");
        pDeleteProgram=(delegate* unmanaged<int,void>)getProcAddress("glDeleteProgram");
        if(pDeleteProgram==null)throw new NotSupportedException("Missing public GL entry point glDeleteProgram.");
        pDeleteShader=(delegate* unmanaged<int,void>)getProcAddress("glDeleteShader");
        if(pDeleteShader==null)throw new NotSupportedException("Missing public GL entry point glDeleteShader.");
        pDeleteVertexArrays=(delegate* unmanaged<int,int*,void>)(version.Major>=3?getProcAddress("glDeleteVertexArrays"):getProcAddress("glDeleteVertexArraysOES"));
        pBindVertexArray=(delegate* unmanaged<int,void>)(version.Major>=3?getProcAddress("glBindVertexArray"):getProcAddress("glBindVertexArrayOES"));
        pGenVertexArrays=(delegate* unmanaged<int,int*,void>)(version.Major>=3?getProcAddress("glGenVertexArrays"):getProcAddress("glGenVertexArraysOES"));
        pGetIntegerv=(delegate* unmanaged<int,int*,void>)getProcAddress("glGetIntegerv");
        if(pGetIntegerv==null)throw new NotSupportedException("Missing public GL entry point glGetIntegerv.");
        pGetStringNative=(delegate* unmanaged<int,IntPtr>)getProcAddress("glGetString");
        if(pGetStringNative==null)throw new NotSupportedException("Missing public GL entry point glGetString.");
        pGetStringiNative=(delegate* unmanaged<int,int,IntPtr>)getProcAddress("glGetStringi");
        pGetError=(delegate* unmanaged<int>)getProcAddress("glGetError");
        if(pGetError==null)throw new NotSupportedException("Missing public GL entry point glGetError.");
        var extensions=new HashSet<string>(StringComparer.Ordinal);
        string? all=GetString(0x1F03);
        if(all!=null)foreach(string e in all.Split(' ',StringSplitOptions.RemoveEmptyEntries))extensions.Add(e);
        else
        {
            GetError(); // Core GL rejects the legacy extension string query.
            GetIntegerv(0x821D,out int count);
            for(int i=0;i<count;i++)if(Marshal.PtrToStringAnsi(GetStringiNative(0x1F03,i)) is {} e)extensions.Add(e);
        }
        ContextInfo=new(version,extensions);
        if(version.Major<3 && !extensions.Contains("GL_OES_vertex_array_object"))
        {
            pGenVertexArrays=null;pBindVertexArray=null;pDeleteVertexArrays=null;
        }
        Version=GetString(0x1F02);Renderer=GetString(0x1F01);Vendor=GetString(0x1F00);
    }
    private readonly delegate* unmanaged<float,float,float,float,void> pClearColor;
    public void ClearColor(float r, float g, float b, float a) { pClearColor(r,g,b,a); }
    private readonly delegate* unmanaged<double,void> pClearDepthDouble;
    public bool IsClearDepthDoubleAvailable=>pClearDepthDouble!=null;
    public void ClearDepthDouble(double value) { if(pClearDepthDouble==null)throw new NotSupportedException("glClearDepth is unavailable."); pClearDepthDouble(value); }
    private readonly delegate* unmanaged<float,void> pClearDepthFloat;
    public bool IsClearDepthFloatAvailable=>pClearDepthFloat!=null;
    public void ClearDepthFloat(float value) { if(pClearDepthFloat==null)throw new NotSupportedException("glClearDepthf is unavailable."); pClearDepthFloat(value); }
    private readonly delegate* unmanaged<int,void> pDepthFunc;
    public void DepthFunc(int value) { pDepthFunc(value); }
    private readonly delegate* unmanaged<byte,void> pDepthMask;
    public void DepthMask(int value) { pDepthMask((byte)value); }
    private readonly delegate* unmanaged<int,void> pClear;
    public void Clear(int bits) { pClear(bits); }
    private readonly delegate* unmanaged<int,int,int,int,void> pViewport;
    public void Viewport(int x, int y, int width, int height) { int scale=SupersamplingFramebuffer!=0 && drawFramebuffer==SupersamplingFramebuffer?2:1;pViewport(x*scale,y*scale,width*scale,height*scale); }
    private readonly delegate* unmanaged<void> pFinish;
    public void Finish() { pFinish(); }
    private readonly delegate* unmanaged<int,int,void> pBindFramebuffer;
    public void BindFramebuffer(int target, int fb) { pBindFramebuffer(target,fb);if(target is 0x8D40 or 0x8CA9)drawFramebuffer=fb; }
    private readonly delegate* unmanaged<int,int> pCheckFramebufferStatus;
    public int CheckFramebufferStatus(int target) { return pCheckFramebufferStatus(target); }
    private readonly delegate* unmanaged<int,int*,void> pGenTextures;
    public void GenTextures(int count, int* res) { pGenTextures(count,res); }
    private readonly delegate* unmanaged<int,int,void> pBindTexture;
    public void BindTexture(int target, int fb) { pBindTexture(target,fb); }
    private readonly delegate* unmanaged<int,void> pActiveTexture;
    public void ActiveTexture(int texture) { pActiveTexture(texture); }
    private readonly delegate* unmanaged<int,int*,void> pDeleteTextures;
    public void DeleteTextures(int count, int* textures) { pDeleteTextures(count,textures); }
    private readonly delegate* unmanaged<int,int> pCreateShader;
    public int CreateShader(int shaderType) { return pCreateShader(shaderType); }
    private readonly delegate* unmanaged<int,int,IntPtr,IntPtr,void> pShaderSource;
    public void ShaderSource(int shader, int count, IntPtr strings, IntPtr lengths) { pShaderSource(shader,count,strings,lengths); }
    private readonly delegate* unmanaged<int,void> pCompileShader;
    public void CompileShader(int shader) { pCompileShader(shader); }
    private readonly delegate* unmanaged<int,int,int*,void> pGetShaderiv;
    public void GetShaderiv(int shader, int name, int* parameters) { pGetShaderiv(shader,name,parameters); }
    private readonly delegate* unmanaged<int,int,int*,void*,void> pGetShaderInfoLog;
    public void GetShaderInfoLog(int shader, int maxLength, out int length, void* infoLog) { int local_length=default; pGetShaderInfoLog(shader,maxLength,&local_length,infoLog);length=local_length; }
    private readonly delegate* unmanaged<int> pCreateProgram;
    public int CreateProgram() { return pCreateProgram(); }
    private readonly delegate* unmanaged<int,int,void> pAttachShader;
    public void AttachShader(int program, int shader) { pAttachShader(program,shader); }
    private readonly delegate* unmanaged<int,void> pLinkProgram;
    public void LinkProgram(int program) { pLinkProgram(program); }
    private readonly delegate* unmanaged<int,int,int*,void> pGetProgramiv;
    public void GetProgramiv(int program, int name, int* parameters) { pGetProgramiv(program,name,parameters); }
    private readonly delegate* unmanaged<int,int,int*,void*,void> pGetProgramInfoLog;
    public void GetProgramInfoLog(int program, int maxLength, out int len, void* infoLog) { int local_len=default; pGetProgramInfoLog(program,maxLength,&local_len,infoLog);len=local_len; }
    private readonly delegate* unmanaged<int,int,IntPtr,void> pBindAttribLocation;
    public void BindAttribLocation(int program, int index, IntPtr name) { pBindAttribLocation(program,index,name); }
    private readonly delegate* unmanaged<int,int*,void> pGenBuffers;
    public void GenBuffers(int len, int* rv) { pGenBuffers(len,rv); }
    private readonly delegate* unmanaged<int,int,void> pBindBuffer;
    public void BindBuffer(int target, int buffer) { pBindBuffer(target,buffer); }
    private readonly delegate* unmanaged<int,IntPtr,IntPtr,int,void> pBufferData;
    public void BufferData(int target, IntPtr size, IntPtr data, int usage) { pBufferData(target,size,data,usage); }
    private readonly delegate* unmanaged<int,int,int,byte,int,IntPtr,void> pVertexAttribPointer;
    public void VertexAttribPointer(int index, int size, int type, int normalized, int stride, IntPtr pointer) { pVertexAttribPointer(index,size,type,(byte)normalized,stride,pointer); }
    private readonly delegate* unmanaged<int,void> pEnableVertexAttribArray;
    public void EnableVertexAttribArray(int index) { pEnableVertexAttribArray(index); }
    private readonly delegate* unmanaged<int,void> pUseProgram;
    public void UseProgram(int program) { pUseProgram(program); }
    private readonly delegate* unmanaged<int,int,IntPtr,void> pDrawArrays;
    public void DrawArrays(int mode, int first, IntPtr count) { pDrawArrays(mode,first,count); }
    private readonly delegate* unmanaged<int,int,int,IntPtr,void> pDrawElements;
    public void DrawElements(int mode, int count, int type, IntPtr indices) { pDrawElements(mode,count,type,indices); }
    private readonly delegate* unmanaged<int,IntPtr,int> pGetUniformLocation;
    public int GetUniformLocation(int program, IntPtr name) { return pGetUniformLocation(program,name); }
    private readonly delegate* unmanaged<int,float,void> pUniform1f;
    public void Uniform1f(int location, float falue) { pUniform1f(location,falue); }
    private readonly delegate* unmanaged<int,int,void> pUniform1i;
    public void Uniform1i(int location, int value) { pUniform1i(location,value); }
    private readonly delegate* unmanaged<int,int,byte,void*,void> pUniformMatrix4fv;
    public void UniformMatrix4fv(int location, int count, bool transpose, void* value) { pUniformMatrix4fv(location,count,(byte)(transpose?1:0),value); }
    private readonly delegate* unmanaged<int,void> pEnable;
    public void Enable(int what) { pEnable(what); }
    private readonly delegate* unmanaged<int,void> pDisable;
    public void Disable(int what) { pDisable(what); }
    private readonly delegate* unmanaged<int,int*,void> pDeleteBuffers;
    public void DeleteBuffers(int count, int* buffers) { pDeleteBuffers(count,buffers); }
    private readonly delegate* unmanaged<int,void> pDeleteProgram;
    public void DeleteProgram(int program) { pDeleteProgram(program); }
    private readonly delegate* unmanaged<int,void> pDeleteShader;
    public void DeleteShader(int shader) { pDeleteShader(shader); }
    private readonly delegate* unmanaged<int,int*,void> pDeleteVertexArrays;
    public bool IsDeleteVertexArraysAvailable=>pDeleteVertexArrays!=null;
    public void DeleteVertexArrays(int count, int* arrays) { if(pDeleteVertexArrays==null)throw new NotSupportedException("glDeleteVertexArrays is unavailable."); pDeleteVertexArrays(count,arrays); }
    private readonly delegate* unmanaged<int,void> pBindVertexArray;
    public bool IsBindVertexArrayAvailable=>pBindVertexArray!=null;
    public void BindVertexArray(int array) { if(pBindVertexArray==null)throw new NotSupportedException("glBindVertexArray is unavailable."); pBindVertexArray(array); }
    private readonly delegate* unmanaged<int,int*,void> pGenVertexArrays;
    public bool IsGenVertexArraysAvailable=>pGenVertexArrays!=null;
    public void GenVertexArrays(int n, int* rv) { if(pGenVertexArrays==null)throw new NotSupportedException("glGenVertexArrays is unavailable."); pGenVertexArrays(n,rv); }
    private readonly delegate* unmanaged<int,int*,void> pGetIntegerv;
    public void GetIntegerv(int name, out int rv) { int local_rv=default; pGetIntegerv(name,&local_rv);rv=local_rv; }
    private readonly delegate* unmanaged<int,IntPtr> pGetStringNative;
    public IntPtr GetStringNative(int v) { return pGetStringNative(v); }
    private readonly delegate* unmanaged<int,int,IntPtr> pGetStringiNative;
    public bool IsGetStringiNativeAvailable=>pGetStringiNative!=null;
    public IntPtr GetStringiNative(int v, int v1) { if(pGetStringiNative==null)throw new NotSupportedException("glGetStringi is unavailable."); return pGetStringiNative(v,v1); }
    private readonly delegate* unmanaged<int> pGetError;
    public int GetError() { return pGetError(); }
    public string? GetString(int name)=>Marshal.PtrToStringAnsi(GetStringNative(name));
    public void ClearDepth(float value)
    {
        // GLES has only glClearDepthf. Some drivers still return a stub for the
        // desktop glClearDepth that raises GL_INVALID_OPERATION when called.
        if(IsClearDepthFloatAvailable)ClearDepthFloat(value);
        else if(IsClearDepthDoubleAvailable)ClearDepthDouble(value);
        else throw new NotSupportedException("No depth clear entry point.");
    }
    public int GenBuffer(){int id;GenBuffers(1,&id);return id;}
    public int GenTexture(){int id;GenTextures(1,&id);return id;}
    public int GenVertexArray(){int id;GenVertexArrays(1,&id);return id;}
    public void DeleteBuffer(int id)=>DeleteBuffers(1,&id);
    public void DeleteTexture(int id)=>DeleteTextures(1,&id);
    public void DeleteVertexArray(int id)=>DeleteVertexArrays(1,&id);
    public void BindAttribLocationString(int program,int index,string name)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(name+"\0");
        fixed(byte* p=bytes)BindAttribLocation(program,index,(nint)p);
    }
    public int GetUniformLocationString(int program,string name)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(name+"\0");
        fixed(byte* p=bytes)return GetUniformLocation(program,(nint)p);
    }
    public string? CompileShaderAndGetError(int shader,string source)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(source);int length=bytes.Length;
        fixed(byte* p=bytes){nint address=(nint)p;ShaderSource(shader,1,(nint)(&address),(nint)(&length));}
        CompileShader(shader);
        int status;GetShaderiv(shader,0x8B81,&status);
        if(status!=0)return null;
        int size;GetShaderiv(shader,0x8B84,&size);
        byte[] log=new byte[Math.Max(1,size)];
        fixed(byte* p=log){GetShaderInfoLog(shader,log.Length,out int written,p);return Encoding.UTF8.GetString(log,0,written);}
    }
    public string? LinkProgramAndGetError(int program)
    {
        LinkProgram(program);
        int status;GetProgramiv(program,0x8B82,&status);
        if(status!=0)return null;
        int size;GetProgramiv(program,0x8B84,&size);
        byte[] log=new byte[Math.Max(1,size)];
        fixed(byte* p=log){GetProgramInfoLog(program,log.Length,out int written,p);return Encoding.UTF8.GetString(log,0,written);}
    }
}
