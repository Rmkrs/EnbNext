using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbOpenGlTextureOwnershipTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Backend = typeof(EnbOpenGlStage0TextureBackend);
    private static FieldInfo Field(string name) => Backend.GetField(name, Private)!;
    private static IDictionary Map(object backend, string name) => (IDictionary)Field(name).GetValue(backend)!;

    private static void Seed(EnbOpenGlStage0TextureBackend backend, bool runtime, int id)
    {
        var owners = Map(backend, runtime ? "_sharedRuntimeTextures" : "_sharedImageTextures");
        var reverse = Map(backend, runtime ? "_runtimeTextureKeys" : "_imageTextureKeys");
        var keyType = owners.GetType().GetGenericArguments()[0];
        object key = runtime
            ? ("fixture", 0, 0, 0, 0, 0, 1f)
            : Activator.CreateInstance(keyType)!;
        owners.Add(key, (id, 2));
        reverse.Add(id, key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgramReleasePreservesTextureOwnersUntilLastMeshRelease(bool runtime)
    {
        var backend = new EnbOpenGlStage0TextureBackend();
        Seed(backend, runtime, 17);
        // Deliberately no native context or GL dispatch table: the program is
        // zero, so this CPU-only test must never call a graphics entry point.
        Assert.Equal(0, Field("_program").GetValue(backend));
        var contextSentinel = (GlInterface)RuntimeHelpers.GetUninitializedObject(typeof(GlInterface));
        backend.Deinitialize(contextSentinel);
        Assert.Single(Map(backend, runtime ? "_runtimeTextureKeys" : "_imageTextureKeys").Keys.Cast<object>());
        var deletes = new Deletes();
        var proc = Field("_deleteTextures");
        proc.SetValue(backend, Delegate.CreateDelegate(proc.FieldType, deletes, typeof(Deletes).GetMethod(nameof(Deletes.Delete))!));
        var release = Backend.GetMethod("DeleteTexture", Private)!;
        release.Invoke(backend, [17]);
        Assert.Empty(deletes.Ids);
        release.Invoke(backend, [17]);
        Assert.Equal(new[] { 17 }, deletes.Ids);
        Assert.Empty(Map(backend, runtime ? "_runtimeTextureKeys" : "_imageTextureKeys").Keys.Cast<object>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextLossForgetsDeviceOwnershipWithoutNativeCalls(bool runtime)
    {
        var backend = new EnbOpenGlStage0TextureBackend();
        Seed(backend, runtime, 17);
        backend.Deinitialize(null);
        Assert.Empty(Map(backend, runtime ? "_runtimeTextureKeys" : "_imageTextureKeys").Keys.Cast<object>());
        Assert.Empty(Map(backend, runtime ? "_sharedRuntimeTextures" : "_sharedImageTextures").Keys.Cast<object>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyContextReplacementDiscardsTextureOwnership(bool runtime)
    {
        var backend = new EnbOpenGlStage0TextureBackend();
        var context = (GlInterface)RuntimeHelpers.GetUninitializedObject(typeof(GlInterface));
        var adopt = Backend.GetMethod("AdoptResourceContext", Private)!;
        adopt.Invoke(backend, [context]);
        Seed(backend, runtime, 17);
        adopt.Invoke(backend, [context]);
        Assert.Single(Map(backend, runtime ? "_runtimeTextureKeys" : "_imageTextureKeys").Keys.Cast<object>());
        var replacement = (GlInterface)RuntimeHelpers.GetUninitializedObject(typeof(GlInterface));
        adopt.Invoke(backend, [replacement]);
        Assert.Empty(Map(backend, runtime ? "_runtimeTextureKeys" : "_imageTextureKeys").Keys.Cast<object>());
        Assert.Empty(Map(backend, runtime ? "_sharedRuntimeTextures" : "_sharedImageTextures").Keys.Cast<object>());
    }

    private sealed class Deletes
    {
        public readonly List<int> Ids = [];
        public void Delete(int count, ref int texture)
        {
            Assert.Equal(1, count);
            Ids.Add(texture);
        }
    }
}