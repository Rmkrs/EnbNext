using System.Runtime.InteropServices;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed unsafe class EnbGlApiTests
{
    private static nint text;
    private static int count,transpose;
    private static float matrixValue;
    private static byte depthMask,normalized;
    private static nint attributePointer;
    [UnmanagedCallersOnly] private static void Depth(byte value)=>depthMask=value;
    [UnmanagedCallersOnly] private static void Attribute(int index,int size,int type,byte norm,int stride,nint pointer)
    {normalized=norm;attributePointer=pointer;}
    [UnmanagedCallersOnly] private static nint String(int name)=>text;
    [UnmanagedCallersOnly] private static void Unused() { }
    [UnmanagedCallersOnly] private static void Matrix(int location,int n,byte transposed,void* values)
    {count=n;transpose=transposed;matrixValue=((float*)values)[15];}
    private static nint Resolve(string name)=>name switch
    {
        "glGetString"=>(nint)(delegate* unmanaged<int,nint>)&String,
        "glUniformMatrix4fv"=>(nint)(delegate* unmanaged<int,int,byte,void*,void>)&Matrix,
        "glDepthMask"=>(nint)(delegate* unmanaged<byte,void>)&Depth,
        "glVertexAttribPointer"=>(nint)(delegate* unmanaged<int,int,int,byte,int,nint,void>)&Attribute,
        _=>(nint)(delegate* unmanaged<void>)&Unused
    };
    [Fact]
    public void NativeMatrixDispatchPreservesCountBooleanAndPointer()
    {
        text=Marshal.StringToHGlobalAnsi("GL_EXT_texture_filter_anisotropic");
        try
        {
            var api=new EnbGlApi(new(EnbGlProfileType.OpenGLES,3,0),Resolve);
            float* values=stackalloc float[16];values[15]=123.5f;
            api.UniformMatrix4fv(7,1,false,values);
            Assert.Equal(1,count);Assert.Equal(0,transpose);Assert.Equal(123.5f,matrixValue);
            api.UniformMatrix4fv(7,2,true,values);
            Assert.Equal(2,count);Assert.Equal(1,transpose);
            Assert.Contains("GL_EXT_texture_filter_anisotropic",api.ContextInfo.Extensions);
            api.DepthMask(1);Assert.Equal((byte)1,depthMask);
            api.DepthMask(0);Assert.Equal((byte)0,depthMask);
            api.VertexAttribPointer(2,3,0x1406,1,20,(nint)12);
            Assert.Equal((byte)1,normalized);Assert.Equal((nint)12,attributePointer);
        }
        finally{Marshal.FreeHGlobal(text);text=0;}
    }
    [Fact]
    public void Es2DoesNotAdvertiseVaoWithoutTheExtensionEvenWhenLoaderReturnsAnAddress()
    {
        text=Marshal.StringToHGlobalAnsi("");
        try
        {
            var api=new EnbGlApi(new(EnbGlProfileType.OpenGLES,2,0),Resolve);
            Assert.False(api.IsGenVertexArraysAvailable);
            Assert.False(api.IsBindVertexArrayAvailable);
            Assert.False(api.IsDeleteVertexArraysAvailable);
            Assert.Throws<NotSupportedException>(()=>api.GenVertexArray());
        }
        finally{Marshal.FreeHGlobal(text);text=0;}
    }
    [Fact]
    public void MissingRequiredProcedureFailsBeforeRendering()
        => Assert.Throws<NotSupportedException>(()=>new EnbGlApi(new(EnbGlProfileType.OpenGLES,3,0),_=>0));
}
