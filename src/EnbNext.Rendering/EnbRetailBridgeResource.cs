using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Installed bridge model and authored attachments, client 0053B7A0.</summary>
public sealed record EnbRetailBridgeResource(string Name, EnbW3dRenderResource Resource)
{
    public IReadOnlyDictionary<string, EnbTextureImage> FailedLoadTextures { get; init; } =
        new Dictionary<string, EnbTextureImage>();
    public IReadOnlyList<string> AnimationLoadFailures { get; init; } = [];
    // 0053B040 initializes BE5AA8 from AEEA60/AEEA70. These are doubles
    // containing float-rounded +/-pi/2, not floats at those addresses.
    // Transposed retail 3x4 storage: Rz(+pi/2) followed by Rx(-pi/2).
    public static Matrix4x4 ModelRoot { get; } =
        Matrix4x4.CreateRotationZ(1.5707963705062866f) *
        Matrix4x4.CreateRotationX(-1.5707963705062866f);

    public static EnbRetailBridgeResource Load(EnbRetailAssetBundle assets, int race)
    {
        // 0053B7A0: race 1/2 select the Jenquai/Progen bridge; 0 and every other value
        // (after an assertion report for the latter) select the Terran bridge.
        string name = race switch { 1 => "j_i_bridge", 2 => "p_i_bridge", _ => "t_i_bridge" };
        var resources = assets.RetailMixResources ?? throw new InvalidDataException("Retail resources unavailable.");
        if (!resources.TryReadResource(name + ".w3d", out var bytes, out var location) ||
            !EnbW3dModel.TryDecode(bytes, out var model)) throw new InvalidDataException("Missing bridge model " + name);
        var textures = model.Meshes.SelectMany(m => m.TextureNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new EnbResolvedTextureReference(n, resources.TryLocateResource(n, out var texture) ? texture : null)).ToArray();
        if (!EnbW3dRenderResourceCompiler.TryCompile(new(location.Name, name + ".w3d", location, model, textures),
            out var resource, out var error)) throw new InvalidDataException(error);
        resource=EnbW3dRenderResourceCache.BindSpecialMaterialFile(assets,new(location.Name,name+".w3d",location,model,textures),resource);
        var failed = new Dictionary<string, EnbTextureImage>(StringComparer.OrdinalIgnoreCase);
        EnbTextureImage? missing = null;
        EnbW3dRenderTextureBinding Resolve(EnbW3dRenderTextureBinding binding)
        {
            if (binding.IsUnbound || assets.Textures.Resolve(binding.LogicalName).Image is not null) return binding;
            missing ??= EnbRetailMissingTexture.Load(assets.Paths.ClientExecutablePath);
            failed[binding.LogicalName] = missing;
            return binding with { ResolveLogicalNameAtRuntime = true };
        }
        resource = resource.Copy().SetMeshes(resource.Meshes.Select(mesh => (mesh).Copy().SetMaterialPasses(mesh.MaterialPasses.Select(pass => (pass).Copy().SetTextureStages(pass.TextureStages.Select(stage => (stage).Copy().SetTextures(stage.Textures.Select(Resolve).ToArray())).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(triangle => triangle with {
                    Textures = triangle.Textures.Select(t => t is null ? null : Resolve(t)).ToArray() }).ToArray())).ToArray())).ToArray());
        return LoadTransitionAnimations(name, resource,
            file => resources.TryReadResource(file, out var data, out _) ? data : null)
            with { FailedLoadTextures = failed };
    }

    internal static EnbRetailBridgeResource LoadTransitionAnimations(string name,
        EnbW3dRenderResource resource, Func<string, byte[]?> read)
    {
        var animations = resource.Animations.ToList();
        var failures = new List<string>();
        // Retail 005DCD20/005DD3C0 fails an individual request, not the bridge.
        // Keep unavailable optional clips local; never substitute a fabricated animation.
        foreach (string suffix in new[] { "-ml", "-mr", "-mf", "-rl", "-lf", "-rf" })
        {
            string file = name + suffix + ".w3d";
            var animationBytes = read(file);
            if (animationBytes is null) continue;
            if (!EnbW3dModel.TryDecode(animationBytes, out var animationModel))
            {
                failures.Add("Invalid bridge animation " + file);
                continue;
            }
            foreach (var animation in animationModel.Animations)
            {
                if (!EnbW3dRenderResourceCompiler.TryCompileAnimation(animations.Count, animation, out var compiled, out var error))
                {
                    failures.Add(file + ": " + error);
                    continue;
                }
                animations.Add(compiled);
            }
        }
        return new(name, resource.Copy().SetAnimations(animations)) { AnimationLoadFailures = failures };
    }

    public Matrix4x4 AuthoredTransform(string attachment, EnbW3dAnimationSample? sample = null)
    {
        var hierarchy = Resource.Hierarchies.Single();
        var pivot = hierarchy.Pivots.Single(p => p.Name.Equals(attachment, StringComparison.OrdinalIgnoreCase));
        var animation = sample is { } s ? Resource.Animations.Single(a => a.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) : null;
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, animation, sample?.Frame ?? 0, out var pose, out var error))
            throw new InvalidDataException(error);
        return pose.WorldTransforms[pivot.Ordinal];
    }

    // 0053BD40 resets the model to BE5AA8 before querying virtual+B4.
    // Camera look therefore consumes this rooted pose, not the raw pivot.
    public Matrix4x4 CameraTransform(string attachment, EnbW3dAnimationSample? sample = null) => AuthoredTransform(attachment, sample) * ModelRoot;
}
