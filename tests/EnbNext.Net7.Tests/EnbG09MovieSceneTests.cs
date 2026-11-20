using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.TestFixtures;

namespace EnbNext.Net7.Tests;

public sealed class EnbG09MovieSceneTests
{
    private sealed class Resources : IEnbRetailMovieResources
    {
        private ulong _next;
        public List<string> Calls = [];
        public void DestroyModel(EnbRetailMovieModel model)=>Calls.Add($"destroy:{model.Identity}");
        public EnbRetailMovieModel? CreateModel(int id, string name, bool ship)
        {
            var file = G08Fixture.Decode(G08Fixture.File());
            var source = new EnbResolvedW3dResource("mapped.w3d", "OBJECT", new("fixture", "fixture", 0, 0), file.Model!, []);
            Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var resource, out var error), error);
            resource = resource.Copy().SetHierarchyModels([new(0, 0x40000, "OBJECT", "RIG", [])]);
            return new(id, ++_next, source, resource) { IsShip = ship };
        }
        public void Animate(EnbRetailMovieModel m, string n, bool c, float s, float e, bool f)
        {
            var animation = m.Resource.Animations.First(a => a.Name == "A");
            m.BindAnimation(animation, m.Resource, c, s, e, f);
        }
        public bool Environment(string c, EnbRetailMovieArguments a) { Calls.Add(c); return true; }
        public bool Effect(string c, EnbRetailMovieArguments a, IReadOnlyList<EnbRetailMovieModel> m, bool p, uint t)
        { Calls.Add(c + ":" + p); return !p; }
        public bool Audio(string c, EnbRetailMovieArguments a, IReadOnlyList<EnbRetailMovieModel> m) { Calls.Add(c); return true; }
        public void Soundtrack(string n) => Calls.Add(n);
        public void ExpireEffect(int id) => Calls.Add("expire");
        public void Damage(EnbRetailMovieModel m, int[] q) => m.SetDamage(q);
        public void Shake(float a, float b) => Calls.Add($"shake:{a}:{b}");
        public void Update(uint t, Matrix4x4 c) { }
        public void Dispose() => Calls.Add("dispose");
    }
    private static bool Execute(EnbRetailMovieScene scene, string text)
    {
        var a = new EnbRetailMovieArguments(text);
        string command = EnbRetailMovieRuntime.CommandNames.First(a.Match);
        return scene.Execute(command, a, false, 1);
    }
    private static EnbRetailMovieRuntime Runtime(EnbRetailMovieScene scene, string text) => new(
        EnbRetailMovieScript.Parse(Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n"))), scene, true);

    [Fact]
    public void DestroyReleasesOnlyFirstMatchingModelDuringTheCommand()
    {
        using var resources=new Resources();using var scene=new EnbRetailMovieScene(resources);
        Assert.True(Execute(scene,"CREATE_OBJECT,7,OBJECT"));Assert.True(Execute(scene,"CREATE_OBJECT,7,OBJECT"));
        var first=scene.Models[0];var second=scene.Models[1];
        Assert.True(Execute(scene,"DESTROY_OBJECT,7"));
        Assert.False(first.IsSceneMember);Assert.Same(second,Assert.Single(scene.Models));
        Assert.Equal(new[]{"destroy:"+first.Identity},resources.Calls);
        Assert.True(Execute(scene,"DESTROY_OBJECT,7"));Assert.Empty(scene.Models);
        Assert.Equal(new[]{"destroy:"+first.Identity,"destroy:"+second.Identity},resources.Calls);
        Assert.False(Execute(scene,"DESTROY_OBJECT,7"));Assert.Equal(2,resources.Calls.Count);
    }

    [Fact]
    public void All58DispositionsReachTheirOwningSceneOrRuntimeConsumer()
    {
        using var resources = new Resources();
        using var scene = new EnbRetailMovieScene(resources);
        string[] commands = [
            "CREATE_OBJECT,1,OBJECT", "CREATE_SHIP_OBJECT,2,OBJECT", "CREATE_POINT_LIGHT,0,1,P1",
            "CREATE_DIRECTIONAL_LIGHT,1", "CREATE_DIRECTIONAL_LIGHT_FROM_BONE,2,1,P1",
            "ATTACH_TO_BONE,2,1,P2", "ATTACH_CAMERA,1,P1", "ADD_CAMERA,1,P2,90,60", "ATTACH_LIGHT_TO_BONE,1,1,P1",
            "PLAY_ANIMATION,1,RIG.A,1,0,-1", "LOAD_ANIMATION,2,RIG.A", "SET_AMBIENT_LIGHT,1,.5,.25",
            "SET_LIGHT_INTENSITY,0,2", "SET_LIGHT_ATTENUATION,0,20", "SET_LIGHT_FAR_ATTENUATION,1,10,100",
            "SET_LIGHT_AMBIENT_COLOR,0,.1,.2,.3", "SET_LIGHT_SPECULAR_COLOR,0,.2,.3,.4", "SET_LIGHT_DIFFUSE_COLOR,0,.3,.4,.5",
            "SET_CAMERA_VIEW_PLANE,90,60", "SET_CLIP_PLANE,2,200", "SHAKE_CAMERA,1,2",
            "SET_SCREEN_FADE_COLOR,1,.5,0,0", "SET_SCREEN_FADE_OPACITY,.5,0", "ENABLE_LETTERBOX,0", "DISABLE_LETTERBOX,0",
            "TURN_ON_MAX_CAMERA", "TURN_OFF_MAX_CAMERA", "TURN_ON_MOUSE_MOVEMENT_OF_CAMERA,45", "TURN_OFF_MOUSE_MOVEMENT_OF_CAMERA",
            "TURN_ON_FOG,1,1,1,10,100,1", "TURN_OFF_FOG", "TURN_ON_ENGINE_TRAILS,1", "TURN_OFF_ENGINE_TRAILS,1",
            "TURN_ON_DAMAGE_PARTICLES,1,3,2", "CHANGE_SHIP_COLOR,1,1,0,0,0,1,0", "MAKE_SHIP_BRONZE,1",
            "MAKE_SHIP_COPPER,1", "MAKE_SHIP_CHROME,1", "ADD_NAME_DECAL,1,Example,1,1,1", "ADD_DECAL,1,100",
            "CREATE_STARS", "CREATE_SKYBOX", "CREATE_ORBITAL,1", "PLAY_AUDIO,TEST,1,P1", "STOP_AUDIO,TEST",
            "CREATE_SFX_ON_OBJECT,1,1", "CREATE_SFX_ON_BONE,1,P1,1", "CREATE_SFX_BETWEEN_OBJECTS,1,2,1",
            "CREATE_SFX_BETWEEN_BONES,1,P1,2,P2,1", "CREATE_SFX_BETWEEN_OBJECT_AND_BONE,1,2,P1,1",
            "SHOW_CURRENT_FRAME", "DONT_SHOW_CURRENT_FRAME", "DEBUG_MODE", "SKIP_FRAMES,30", "HIDE_OBJECT,2", "UNHIDE_OBJECT,2",
            "DESTROY_OBJECT,2", "END_MOVIE"
        ];
        Assert.Equal(EnbRetailMovieRuntime.CommandNames.Order(), commands.Select(c => c.Split(',')[0]).Order());
        using var runtime = Runtime(scene, string.Join("\n", commands.Select((c, i) => $"{i + 1} {c}")) + "\n");
        runtime.Update(0);
        for (int i = 0; i < commands.Length; i++) runtime.Update(34);
        Assert.Null(runtime.FailedCommand); Assert.True(runtime.Ended); Assert.Equal(0, runtime.RemainingCommands);
        var model = Assert.Single(scene.Models);
        Assert.Equal("EXAMPLE", model.NameDecal!.Name);
        Assert.Equal(2, model.Decals.Length); Assert.Equal(4, model.Colors.Length);
        Assert.Equal(4, model.Snapshot(0).ColorizationAudit!.ApplyCount);
        Assert.Equal(1, model.Snapshot(0).NameDecalApplyCount);
        Assert.Equal(4, model.Snapshot(0).NameDecalColorizationApplyCount);
        Assert.Equal(1, model.Snapshot(0).DecalApplyCount);
        Assert.Equal(new EnbRetailMovieDamage(3, 1), model.Damage[2]);
        Assert.False(model.EngineTrails); Assert.False(scene.FogEnabled); Assert.False(scene.MouseMovement);
        Assert.Equal(new Vector2(2, 200), scene.Clip); Assert.Equal(2, scene.Lights[0]!.Intensity);
        Assert.Equal(new Vector2(20), scene.Lights[0]!.FarAttenuation);
        Assert.Equal(5, resources.Calls.Count(c => c.EndsWith(":True")));
        Assert.Equal(5, resources.Calls.Count(c => c.EndsWith(":False")));
    }

    [Fact]
    public void FailureKeepsNativePartialMutationsAndFirstDuplicateIdentity()
    {
        using var scene = new EnbRetailMovieScene(new Resources());
        Assert.True(Execute(scene, "CREATE_OBJECT,1,A")); Assert.True(Execute(scene, "CREATE_OBJECT,1,B"));
        Assert.True(Execute(scene, "CREATE_OBJECT,2,C"));
        var first = scene.Models[0]; var child = scene.Models[2];
        Assert.False(Execute(scene, "ATTACH_TO_BONE,1,1,P1"));
        Assert.True(Execute(scene, "ATTACH_TO_BONE,2,1,P2")); Assert.Same(first, child.Parent);
        Assert.False(Execute(scene, "ATTACH_TO_BONE,2,1")); Assert.Null(child.Parent); Assert.False(child.IsSceneMember);
        Assert.True(Execute(scene, "CREATE_POINT_LIGHT,0")); Assert.False(scene.Lights[0]!.InScene);
        Assert.False(Execute(scene, "CREATE_DIRECTIONAL_LIGHT,0,1")); Assert.True(scene.Lights[0]!.Directional);
        Assert.True(Execute(scene, "HIDE_OBJECT,1")); Assert.True(first.Hidden); Assert.False(scene.Models[1].Hidden);
        Assert.True(Execute(scene, "DESTROY_OBJECT,1")); Assert.DoesNotContain(first, scene.Models);
        Assert.False(Execute(scene, "TURN_ON_DAMAGE_PARTICLES,1,5"));
    }

    [Fact]
    public void DirectionalFromBoneWithoutSourceRetainsDefaultAndMovieContinues()
    {
        using var scene = new EnbRetailMovieScene(new Resources());
        using var runtime = Runtime(scene, "0 CREATE_DIRECTIONAL_LIGHT_FROM_BONE,0\n1 SET_LIGHT_INTENSITY,0,2\n2 END_MOVIE\n");
        runtime.Update(0);
        var light = Assert.IsType<EnbRetailMovieLight>(scene.Lights[0]);
        Assert.True(light.Directional); Assert.True(light.InScene);
        Assert.Equal(Vector3.UnitZ, light.Direction); Assert.Null(light.Parent);
        runtime.Update(100);
        Assert.Null(runtime.FailedCommand); Assert.True(runtime.Ended);
        Assert.Equal(2, light.Intensity);
        Assert.False(Execute(scene, "CREATE_DIRECTIONAL_LIGHT_FROM_BONE,0,99"));
        Assert.True(scene.Lights[0]!.InScene);
    }

    [Fact]
    public void AnimationAttachmentDamageAndCameraRetainTheirSeparateLifetimes()
    {
        using var scene = new EnbRetailMovieScene(new Resources());
        Execute(scene, "CREATE_OBJECT,1,A"); Execute(scene, "CREATE_OBJECT,2,B");
        Execute(scene, "ATTACH_TO_BONE,2,1,P1"); Execute(scene, "ATTACH_CAMERA,1,P1");
        Execute(scene, "PLAY_ANIMATION,1,RIG.A,0,0,-1"); scene.Update(50);
        Assert.True(scene.Models[1].TryWorld(out var pose)); Assert.Equal(12, pose.Translation.X);
        Assert.Equal(pose.Translation, scene.Camera.Translation);
        Execute(scene, "LOAD_ANIMATION,1,RIG.A"); scene.Update(1000); Assert.Equal(0, scene.Models[0].Frame);
        Execute(scene, "TURN_ON_DAMAGE_PARTICLES,1,4"); Execute(scene, "TURN_ON_DAMAGE_PARTICLES,1,0");
        Assert.All(scene.Models[0].Damage, d => Assert.Equal(new EnbRetailMovieDamage(4, 1), d));
        Execute(scene, "TURN_ON_DAMAGE_PARTICLES,1,1,2"); Assert.Equal(new EnbRetailMovieDamage(1, 2), scene.Models[0].Damage[2]);
        Execute(scene, "ADD_CAMERA,1,P2,90,60"); scene.CycleCamera(); scene.Update(0);
        Assert.Equal(Vector3.Zero, scene.Camera.Translation); Assert.InRange(scene.FieldOfView.X, 1.57f, 1.58f);
    }
}
