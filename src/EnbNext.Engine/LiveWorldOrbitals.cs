using System.Numerics;
using System.Runtime.InteropServices;

using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SetDepthRange(double near, double far);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SetDepthRangeFloat(float near, float far);

    private Action<float, float>? _setOrbitalDepthRange;
    private string _orbitalDrawStatus = "orbitals inactive";
    private EnbRetailOrbitalFogEvaluation _currentOrbitalEvaluation;
    private bool _currentOrbitalEvaluationResolved;

    // Render-only owners, never inserted into the session snapshot or radar.
    private readonly Dictionary<(uint Sector, int Index), ulong> _orbitalRenderIdentities = [];
    private ulong _nextOrbitalRenderIdentity = ulong.MaxValue;

    private EnbResolvedRenderCamera? AppendOrbitalRoots(
        LiveFrame frame, EnbRetailAssetBundle assets, EnbRetailBaseAssetResolver resolver,
        RootTaskList roots)
    {
        _orbitalDrawStatus = "orbitals inactive";
        EnbRetailOrbitalSceneDefinition scene;
        uint sector;
        int mode;
        if (frame.Movie?.Orbitals is { } movieScene) { scene = movieScene; sector = 0; mode = 0; }
        else
        {
            if (!_useMainViewEnvironment || frame.Snapshot.IsTransitioning ||
                frame.Snapshot.Environment is not { PresentationMode: 0 or 1 or 5 } environment ||
                !_orbitalScenes.TryGetValue(environment.SectorNum, out scene!)) return null;
            sector = environment.SectorNum; mode = environment.PresentationMode.Value;
        }

        PrepareOrbitalFactories(scene,frame,assets);
        var staged = new RootTaskList();
        var spheres = new List<EnbW3dWorldBounds>();
        var owners = new List<RootTaskList>();
        var visibilityOwners = new List<(EnbRetailOrbitalDefinition Orbital, float Elevation)>();
        float minimum = float.PositiveInfinity, maximum = 0;
        int orbitalTime = frame.Snapshot.GetOrbitalTimeMilliseconds(frame.CurrentClientTime);
        float hours = frame.Movie?.OrbitalHours ?? orbitalTime * EnbRetailOrbitalFogEvaluator.CurrentRetailHoursPerMillisecond;
        var dominant = _currentOrbitalEvaluation.DominantLight?.Enabled == true
            ? scene.Orbitals.FirstOrDefault(o => o.Index == _currentOrbitalEvaluation.DominantOrbitalIndex) : null;
        int rejected = 0;
        string? failure = null;
        foreach (var orbital in scene.Orbitals)
        {
            string? diagnostic = null;
            LiveCpuRoot? root = null;
            var factory=ResolveOrbitalFileFactory(orbital,assets);
            var resolution = factory?.Factory.Resolution??ResolveBaseAsset(resolver, orbital.BaseAsset);
            bool isNull = resolution.Status == EnbBaseAssetResolutionStatus.NullObject;
            bool isRsl = resolution.Rsl is not null;
            if(factory is not null) {root=factory.Factory.Root;diagnostic=factory.Factory.Diagnostic;}
            else if (resolution.W3d is { } w3d)
            {
                if (frame.Movie is not null && w3d.CurrentRetailAggregate is null)
                {
                    if (!_prototypeResources.TryFindPrototypeSource(w3d.RequestedObjectName, out var published))
                    {
                        _prototypeResources.TryLoadFile(w3d, out _, out diagnostic, ReadMapperClock());
                        _prototypeResources.TryFindPrototypeSource(w3d.RequestedObjectName, out published);
                    }
                    if (published is not null) w3d = published;
                }
                TryGetCpuRoot(new LiveCpuRootKey(orbital.BaseAsset, -1, w3d.LogicalFileName, w3d.RequestedObjectName),
                    w3d, assets, out root!, out diagnostic);
            }
            else if (resolution.Rsl is { } rsl)
                TryGetInitialRslRoot(orbital.BaseAsset, rsl, assets, out root!, out diagnostic);

            if ((!isNull && root is null) || !EnbRetailOrbitalFogEvaluator.TryBuildOrbitalWorldTransform(
                    orbital, hours, frame.Camera.Position, out var rigidWorld, out _, out var elevation, out _) ||
                !TryResolveOrbitalWorldSphere(resolver, assets, orbital, rigidWorld, out var sphere, out diagnostic))
            {
                rejected++;
                failure ??= diagnostic ?? $"orbital {orbital.Index} model/transform unavailable";
                continue;
            }

            // 004F34C0 computes radial limits from the scaled RenderObj sphere;
            // 0050E330 converts the minimum to depth at the frustum corners.
            minimum = MathF.Min(minimum, orbital.Distance - sphere.Radius);
            maximum = MathF.Max(maximum, orbital.Distance + sphere.Radius);
            if (isNull)
            {
                //Native NULL keeps its sphere in orbital clipping/visibility,
                //but its draw and material/day-night traversal emit nothing.
                spheres.Add(sphere);
                visibilityOwners.Add((orbital, elevation));
                owners.Add([]);
                continue;
            }
            var identityKey = (sector, orbital.Index);
            if (!_orbitalRenderIdentities.TryGetValue(identityKey, out ulong identity))
                _orbitalRenderIdentities.Add(identityKey, identity = _nextOrbitalRenderIdentity--);
            var position = rigidWorld.Translation;
            var obj = new EnbSceneObjectSnapshot(identity, (ulong)_assetGeneration, 0, false, false,
                byte.MaxValue, orbital.BaseAsset, factory?.Scale??orbital.Scale,
                factory?.Hsv.X??orbital.HsvH, factory?.Hsv.Y??orbital.HsvS, factory?.Hsv.Z??orbital.HsvV,
                0, false, $"orbital {orbital.Index}", new(false, 0, null),
                new(EnbSceneSpatialProviderKind.Fixed,
                    new(frame.CurrentClientTime, position, Quaternion.CreateFromRotationMatrix(rigidWorld),
                        Vector3.Zero, 0, Vector3.Zero, Vector3.UnitX), null),
                null, null, [], [], [], [], [], [], null, [], null, 0, 0);

            float? phase = null;
            if (frame.Movie is not null || orbital.PhasePerHour != 0 || mode == 1)
                phase = hours * orbital.PhasePerHour;
            else if (dominant?.Index == orbital.Index)
                phase = null; //004F4C85 skips the selected emitter's AdjustDayNight.
            else if (dominant is not null && EnbRetailOrbitalFogEvaluator.TryBuildOrbitalWorldTransform(
                         dominant, hours, frame.Camera.Position, out var lightWorld, out _, out _, out _) &&
                     EnbRetailOrbitalFogEvaluator.TryComputeOrdinarySceneDayNightPhase(position,
                         frame.Camera.Position, lightWorld.Translation == position ? Vector3.Zero : Vector3.Normalize(lightWorld.Translation - position), out float computed))
                phase = computed;
            else
            {
                rejected++;
                failure ??= $"orbital {orbital.Index} dominant light is unresolved";
                continue;
            }

            var objectRoots = new RootTaskList();
            if (!TryAppendRootInstanceTreePrepared(obj, root!, isRsl,
                    Matrix4x4.CreateScale(factory?.Scale??orbital.Scale) * rigidWorld, "orbital", true, null,
                    objectRoots, out diagnostic, initialize: instance =>
                    {
                        //4F2960 configures the model through6F5150 before its
                        //rigid orbital placement. The retained hierarchy and
                        //camera-facing mesh consumer need those separate inputs,
                        //just like ordinary factories; the world basis is not
                        //a substitute (97A230 discards it for oriented meshes).
                        instance.PrimitiveFactoryWorld=rigidWorld;
                        instance.PrimitiveFactoryScale=obj.Scale;
                        instance.PrimitiveFactoryHsv=new(obj.HsvH,obj.HsvS,obj.HsvV);
                        instance.HierarchySourceScale=1;
                    }))
            {
                rejected++;
                failure ??= diagnostic;
                continue;
            }
            foreach (var instance in objectRoots)
            {
                instance.OrbitalPhase = phase;
                instance.OrbitalForceVisible = orbital.ForceVisible;
            }
            staged.AddRange(objectRoots);
            spheres.Add(sphere);
            visibilityOwners.Add((orbital, elevation));
            owners.Add(objectRoots);
        }

        for (int i = 0; i < owners.Count; i++)
        {
            var owner = visibilityOwners[i];
            float visibility = EnbRetailOrbitalFogEvaluator.ComputeDazzleVisibility(mode, owner.Orbital,
                owner.Elevation, frame.Camera.Position, i, spheres);
            foreach (var instance in owners[i]) instance.OrbitalVisibility = visibility;
        }
        minimum = MathF.Max(1, minimum);
        maximum = MathF.Max(minimum + 1, maximum);
        var projection = frame.Camera.Projection;
        float near = minimum / MathF.Sqrt(1 + (float)(1 / (projection.Matrix.M11 * projection.Matrix.M11)) +
                                              (float)(1 / (projection.Matrix.M22 * projection.Matrix.M22)));
        if (owners.Count == 0 || !EnbResolvedRenderCamera.TryCreate(frame.Camera.Position,
                frame.Camera.WorldOrientation, frame.Camera.Viewport, projection.HorizontalFieldOfViewRadians,
                projection.AspectRatio, new(near, maximum), out var camera))
        {
            _orbitalDrawStatus = $"orbitals rejected {rejected}: {failure}";
            return null;
        }
        foreach (var instance in staged)
            instance.OrbitalCamera = camera;
        roots.AddRange(staged);
        _orbitalDrawStatus = $"orbitals roots {staged.Count}/{scene.Orbitals.Count} reject {rejected} clock {orbitalTime}ms clip {near:0}..{maximum:0}" +
                             (failure is null ? "" : $" ({failure})");
        return camera;
    }

    private bool _retainOrbitalDepthRange;
    private (float Near,float Far)? _retainedOrbitalDepthRange;
    private readonly struct OrbitalDepthRangeScope : IDisposable
    {
        private readonly EnbWorldRenderer _owner;
        private readonly bool _previous;
        public OrbitalDepthRangeScope(EnbWorldRenderer owner)
        {_owner=owner;_previous=owner._retainOrbitalDepthRange;owner._retainOrbitalDepthRange=true;owner._retainedOrbitalDepthRange=null;}
        public void Dispose(){_owner._retainOrbitalDepthRange=_previous;_owner._retainedOrbitalDepthRange=null;}
    }

    private void ApplyOrbitalDepthRange(EnbGlApi gl, bool orbital, float primaryMaximum = .95f, float orbitalMinimum = .95f)
    {
        if (_setOrbitalDepthRange is null)
        {
            IntPtr address = gl.GetProcAddress("glDepthRangef");
            if (address != IntPtr.Zero)
            {
                var set = Marshal.GetDelegateForFunctionPointer<SetDepthRangeFloat>(address);
                _setOrbitalDepthRange = (near, far) =>
                {
                    if(_retainOrbitalDepthRange && _retainedOrbitalDepthRange==(near,far))return;
                    set(near,far);
                    _retainedOrbitalDepthRange=_retainOrbitalDepthRange?(near,far):null;
                };
            }
            else
            {
                address = gl.GetProcAddress("glDepthRange");
                if (address == IntPtr.Zero)
                    throw new NotSupportedException("OpenGL orbital pass requires depth range support.");
                var set = Marshal.GetDelegateForFunctionPointer<SetDepthRange>(address);
                _setOrbitalDepthRange = (near, far) =>
                {
                    if(_retainOrbitalDepthRange && _retainedOrbitalDepthRange==(near,far))return;
                    set(near,far);
                    _retainedOrbitalDepthRange=_retainOrbitalDepthRange?(near,far):null;
                };
            }
        }
        // MainView mode 0: +11C uses .95..1, +10C uses 0...95.
        _setOrbitalDepthRange(orbital ? orbitalMinimum : 0, orbital ? 1 : primaryMaximum);
    }
}
