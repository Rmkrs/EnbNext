using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Current client 00BB1218/00BB1288 and hover-clone shader 00496620.</summary>
public static class EnbCurrentRetailVerbPresentation
{
    public static bool IsEnabled(uint verb) => (verb & 0xffff) == 0;

    public static string Format(uint verb)
    {
        string name = (verb >> 16) switch
        {
            0 => "NA", 1 => "Scan", 2 => "Land", 3 => "Loot", 4 => "Group",
            5 => "Message", 6 => "Trade", 7 => "Tractor", 8 => "Dock",
            9 => "Prospect", 10 => "Gate", 11 => "Register", 12 => "Jumpstart",
            13 => "Follow", _ => "unknown",
        };
        if (name == "unknown") return name;
        return name + ((verb & 0xffff) switch
        {
            1 => ": Player already in a group", 2 => ": Too Far", 0xffff => ": Unavailable", _ => "",
        });
    }

    public static uint HoverShader(uint packed) => (packed & 0xffff7f3fu) | 0x4020u;

    // 0055E070 -> 00496490(actor, 0): copy diffuse RGB to emissive in place.
    // Unlike 00496620 button hover, this neither clones geometry nor changes blending.
    public static EnbW3dRenderMaterialPass ApplyAvatarHighlight(EnbW3dRenderMaterialPass pass)
        => ChangeEmission(pass, s => s.Uniforms.Diffuse);

    // 0066C2D0 / 0066C860: held mouse press uses RGB .35, not a timed flash.
    public static EnbW3dRenderMaterialPass ApplyPressed(EnbW3dRenderMaterialPass pass)
        => ChangeEmission(pass, _ => new Vector3(.35f));

    private static EnbW3dRenderMaterialPass ChangeEmission(EnbW3dRenderMaterialPass pass,
        Func<EnbW3dCompiledMaterialSemantics, Vector3> color)
    {
        EnbW3dCompiledMaterialSemantics Change(EnbW3dCompiledMaterialSemantics s)
            => s with { Uniforms = s.Uniforms with { Emissive = color(s) } };
        return (pass).Copy().SetSingleBindingSemantics(pass.SingleBindingSemantics is {} s ? Change(s) : null).SetTriangleBindings(pass.TriangleBindings.Select(t => t with
                { Semantics = t.Semantics is {} s ? Change(s) : null }).ToArray());
    }

    public static EnbW3dRenderMaterialPass Apply(EnbW3dRenderMaterialPass pass, bool highlight, bool disabled,
        float highlightStrength = 1f)
    {
        if (!highlight && !disabled) return pass;
        // 00496620 -> 00497350 -> 00946D00 writes only descriptor shader[0].
        // 00946FD0 still selects an expanded triangle array ahead of that slot.
        bool changeUniform = highlight && pass.Ordinal == 0;
        bool changeEffective = changeUniform && pass.ShaderCardinality != EnbW3dRenderBindingCardinality.PerTriangle;
        uint uniformShader = changeUniform ? HoverShader(pass.CurrentRetailUniformShader) : pass.CurrentRetailUniformShader;
        EnbW3dCompiledMaterialSemantics Change(EnbW3dCompiledMaterialSemantics s)
        {
            var state = s.FixedFunction;
            if (changeEffective && !EnbW3dMaterialCompiler.TryDecodePackedRetailState(
                    uniformShader, out state, out var error))
                throw new InvalidOperationException(error);
            return s with
            {
                FixedFunction = state,
                Uniforms = s.Uniforms with { Emissive = new Vector3(disabled ? .35f : highlightStrength) },
                PermutationKey = s.PermutationKey with { TextureCombinerSemantics = state.TextureCombinerSemantics },
            };
        }
        EnbW3dRenderShaderBinding ChangeShader(EnbW3dRenderShaderBinding shader)
        {
            if (!changeEffective) return shader;
            if (!EnbW3dMaterialCompiler.TryDecodePackedRetailState(uniformShader,
                    out var state, out var error)) throw new InvalidOperationException(error);
            return shader with { FixedFunction = state, SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState };
        }
        return (pass).Copy().SetCurrentRetailUniformShader(uniformShader).SetSingleBindingSemantics(pass.SingleBindingSemantics is { } single ? Change(single) : null).SetShaders(EnbW3dShaderBindingTable.Freeze(pass.Shaders.Select(ChangeShader))).SetTriangleBindings(pass.TriangleBindings.Select(t => t with
            {
                Semantics = t.Semantics is { } s ? Change(s) : null,
                Shader = t.Shader is { } shader ? ChangeShader(shader) : null,
            }).ToArray());
    }

    public static EnbW3dProgrammableTextureLanePlan ApplyShortcutRange(EnbW3dProgrammableTextureLanePlan lane,int frame,float angle)
    {
        var lighting=lane.LightingPlan;var original=lane.Submission.Packet;
        var rotation=System.Numerics.Matrix4x4.CreateRotationZ(-angle);
        System.Numerics.Matrix4x4.Invert(original.WorldTransform,out var inverse);
        var world=original.WorldTransform*rotation;var delta=world*inverse;
        var packet=original.ForkTask().SetWorldTransform(world).SetCurrentRetailEffectMapper(EnbW3dMapperRuntimeEvaluation.Identity with
            {TextureOffset=new((frame&3)*.25f,(frame>>2)*.25f)});
        return lane.Copy().SetLightingPlan(lighting.Copy().SetSubmission(lane.Submission with {Packet=packet})
            .SetModelToWorld(world).SetCanonicalClipFromModel(delta*lighting.CanonicalClipFromModel)
            .SetCanonicalViewFromModel(delta*lighting.CanonicalViewFromModel));
    }
}
