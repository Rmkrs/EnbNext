using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Bounded unary SFX boundary, current 006E70F0 / 006E7330.</summary>
public static class EnbCurrentRetailEffectSoundRuntime
{
    // Unary006E7690 and binary006E7ED0 clear object pointers permanently;
    // another object using that network ID must not supply a fresh position.
    public static bool IsOriginalOwner(EnbCurrentRetailEffectSoundNode node,uint id,Func<uint,ulong?>? findIdentity)
    {
        if(findIdentity is null)return true;
        ulong? expected=id==node.Root.SourceGameId ? node.SourceRenderIdentity ?? node.Root.InitialSourceObject?.RenderIdentity ?? node.Root.PublishedEndpoints?.Source.RenderIdentity
            : id==node.Root.TargetGameId ? node.TargetRenderIdentity ?? node.Root.PublishedEndpoints?.Target.RenderIdentity : null;
        return expected is null || findIdentity(id)==expected;
    }

    // LineEffect00AFFF4C+8 ->0040B4EC ->006E5720 ->008DE5C0.
    // Use the binary endpoint coordinates before visible growth/shrink.
    public static Vector3 ClosestLinePosition(Vector3 start,Vector3 end,Vector3 listener)
    {
        var delta=end-start;float lengthSquared=delta.LengthSquared();
        float projection=lengthSquared!=0?Vector3.Dot(listener-start,delta)/lengthSquared:0;
        // 008DE5F4 chooses the stored start endpoint for unordered comparisons;
        // neither endpoint branch evaluates start + delta * 0/1.
        return !(projection>0)?start:projection>=1?end:start+delta*projection;
    }
    public static bool TryResolvePosition(EnbCurrentRetailEffectSoundNode node,
        Func<uint,Vector3?> findPosition,out Vector3 position,out bool follows,out string? error,
        Func<uint,string,Vector3?>? findBonePosition=null, bool radiusWorldPosition=false,
        Func<EnbCurrentRetailEffectSoundNode,Vector3?>? findEffectPosition=null, Vector3? listenerPosition=null)
    {
        position=default; follows=true; error=null;
        if(node.ReleasedWorldPosition is {} released)
        {
            if(!float.IsFinite(released.X)||!float.IsFinite(released.Y)||!float.IsFinite(released.Z))
            {error="non-finite released sound position";return false;}
            position=released;follows=false;return true;
        }
        var d=node.Definition;
        if (d.Properties.ContainsKey("TriggerSFXRadius"))
        {
            if (!EnbRetailIniDocument.TryReadFloat(d.Properties,"TriggerSFXRadius",out float radius) || !float.IsFinite(radius))
            { error="invalid sound trigger radius"; return false; }
            if (radius>=0 && !radiusWorldPosition) { error="radius sound requires its scheduled world-position path"; return false; }
        }
        if (Is2D(d) && !radiusWorldPosition) return true;
        // Sound006E9360, Pass006EDB70 and RObj006EF120 read the retained unary
        // translation forwarded by006E78A0, before visual camera placement.
        if(!node.ClientDamage && d.EffectClassKind is (EnbEffectClassKind.SoundEffect or EnbEffectClassKind.PassEffect or EnbEffectClassKind.RObjEffect) &&
            (node.Root.GeometryKind==EnbSceneEffectGeometryKind.Object ||
             (node.Root.IsObjectToObject || node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint) && node.LinkKind!=EnbCurrentRetailEffectLinkKind.Root) &&
            findEffectPosition?.Invoke(node) is {} linkedSoundPosition)
        {
            if(!float.IsFinite(linkedSoundPosition.X) || !float.IsFinite(linkedSoundPosition.Y) || !float.IsFinite(linkedSoundPosition.Z))
            {error="non-finite linked sound position";return false;}
            position=linkedSoundPosition;
            if(d.Properties.ContainsKey("TrackSource")) follows=EnbRetailIniDocument.ReadBool(d.Properties,"TrackSource");
            return true;
        }
        // Constructor coordinates survive a missing unary descriptor. Binary
        // getters006EFF20/006E5720 still return those endpoints, not packet IDs.
        if(node.EffectOwner is {HasUnboundEndpoints:true,LinkKind:EnbCurrentRetailEffectLinkKind.Root})
        {position=Vector3.Zero;follows=false;return true;}
        if (radiusWorldPosition && d.EffectClassKind != EnbEffectClassKind.SoundEffect)
        { error="radius sound retained effect position unavailable"; return false; }
        if (d.EffectClassKind is EnbEffectClassKind.RObjLookAtEffect or EnbEffectClassKind.ParticleJetEffect)
        {
            if(findEffectPosition is not null)
            {
                if(findEffectPosition(node) is not Vector3 retained || !float.IsFinite(retained.X) || !float.IsFinite(retained.Y) || !float.IsFinite(retained.Z))
                {error="binary sound retained endpoints unavailable";return false;}
                position=retained;return true;
            }
            // 006EFF20 selects the binary source/target coordinates using
            // SourceBased. 006ECFF0 -> 008DE5C0 instead finds the point on
            // that segment closest to the camera passed by 006E70F0.
            Vector3? source=string.IsNullOrEmpty(node.Root.BoneName)
                ? findPosition(node.Root.SourceGameId) : findBonePosition?.Invoke(node.Root.SourceGameId,node.Root.BoneName);
            if(string.IsNullOrEmpty(node.Root.BoneName))source ??= node.Root.LastSourceObject?.Spatial?.Transform.Position;
            Vector3? target=node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint?node.Root.WorldPoint:
                findPosition(node.Root.TargetGameId) ?? node.Root.PublishedEndpoints?.Target.Spatial?.Transform.Position;
            if(source is not Vector3 start || target is not Vector3 end)
            { error="binary sound endpoints unavailable"; return false; }
            end+=node.Root.SpatialOffset;
            if(d.EffectClassKind==EnbEffectClassKind.RObjLookAtEffect)
                position=EnbRetailIniDocument.ReadBool(d.Properties,"SourceBased")?start:end;
            else
            {
                if(listenerPosition is not Vector3 listener)
                { error="particle jet sound camera unavailable"; return false; }
                position=ClosestLinePosition(start,end,listener);
            }
            if(!float.IsFinite(position.X)||!float.IsFinite(position.Y)||!float.IsFinite(position.Z))
            { error="non-finite binary sound position"; return false; }
            return true;
        }
        // Attach 00AFFE88+8 -> 006E4BA0 returns its stored unary translation;
        // activation refresh is distinct from subsequent TrackSource updates.
        if(d.EffectClassKind==EnbEffectClassKind.AttachEffect)
        {
            if(findEffectPosition?.Invoke(node) is not Vector3 attached ||
                !float.IsFinite(attached.X)||!float.IsFinite(attached.Y)||!float.IsFinite(attached.Z))
            {error="attachment sound bound position unavailable";return false;}
            position=attached;
            follows=EnbRetailIniDocument.ReadBool(d.Properties,"TrackSource");
            return true;
        }
        // StraightSeek 00B003EC+8 and SplineRandomSeek 00B00484+8 -> 006F06E0 return
        // the effect render object's translation, not either endpoint.
        if (d.EffectClassKind is EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect)
        {
            if (findEffectPosition?.Invoke(node) is not Vector3 rendered ||
                !float.IsFinite(rendered.X) || !float.IsFinite(rendered.Y) || !float.IsFinite(rendered.Z))
            { error="projectile sound render position unavailable"; return false; }
            position=rendered;
            return true;
        }
        // Linked RObj audio reads the bound matrix before camera placement
        // (00B001C0+8 -> 006EF120). The production pose cache contains that
        // matrix, including 006EFC20's retained projectile-to-impact copy.
        // Packet-root bone/offset and BoundGameId do not describe this copy.
        if (d.EffectClassKind == EnbEffectClassKind.RObjEffect && node.Root.IsObjectToObject &&
            node.LinkKind != EnbCurrentRetailEffectLinkKind.Root)
        {
            if (findEffectPosition?.Invoke(node) is not Vector3 bound ||
                !float.IsFinite(bound.X) || !float.IsFinite(bound.Y) || !float.IsFinite(bound.Z))
            { error="linked RObj sound bound position unavailable"; return false; }
            position=bound;
            if (d.Properties.ContainsKey("TrackSource")) follows=EnbRetailIniDocument.ReadBool(d.Properties,"TrackSource");
            return true;
        }
        // The concrete LineEffect getter projects the listener onto the
        // retained binary segment. It is not a source-object sound anchor.
        if (d.EffectClassKind == EnbEffectClassKind.LineEffect && (node.EffectOwner?.HasUnboundEndpoints==true || node.Root.IsObjectToObject || node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint))
        {
            if(findEffectPosition is not null)
            {
                if(findEffectPosition(node) is not Vector3 bound || !float.IsFinite(bound.X)||!float.IsFinite(bound.Y)||!float.IsFinite(bound.Z))
                {error="line sound retained segment unavailable";return false;}
                position=bound;return true;
            }
            Vector3? source=string.IsNullOrEmpty(node.Root.BoneName)?findPosition(node.Root.SourceGameId):findBonePosition?.Invoke(node.Root.SourceGameId,node.Root.BoneName);
            Vector3? target=node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint?node.Root.WorldPoint:findPosition(node.Root.TargetGameId);
            if(source is not Vector3 start || target is not Vector3 end || listenerPosition is not Vector3 listener)
            {error="line sound endpoints/listener unavailable";return false;}
            position=ClosestLinePosition(start,end+node.Root.SpatialOffset,listener);
            return true;
        }
        if (d.EffectClassKind is not (EnbEffectClassKind.SoundEffect or EnbEffectClassKind.PassEffect or EnbEffectClassKind.RObjEffect))
        { error=$"{d.EffectClass} sound-position handler is not implemented"; return false; }
        if (!string.IsNullOrEmpty(node.Root.BoneName) || node.Root.SpatialOffset!=Vector3.Zero)
        { error="bone/offset sound binding is not implemented"; return false; }
        if (d.Properties.ContainsKey("TrackSource")) follows=EnbRetailIniDocument.ReadBool(d.Properties,"TrackSource");
        if (node.BoundGameId is uint id)
        {
            Vector3? source = findPosition(id);
            if(source is null && id==node.Root.SourceGameId)
            {
                source=node.Root.LastSourceObject?.Spatial?.Transform.Position;
                if(source is not null) follows=false;
            }
            if(source is null && id==node.Root.TargetGameId && node.Root.PublishedEndpoints is {} endpoints)
            {
                // Linked unary effects retain their bound endpoint when its
                // object is removed (006E7690 / 006E7870). Publication captures
                // that transform before render frames can skip over removal.
                source=endpoints.Target.Spatial?.Transform.Position;
                if(source is not null)follows=false;
            }
            if (source is not Vector3 found) { error=$"source 0x{id:X8} is unavailable"; return false; }
            position=found;
        }
        else if (node.Root.GeometryKind == EnbSceneEffectGeometryKind.Point) position=node.Root.WorldPoint;
        else { error="no supported unary binding"; return false; }
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        { error="non-finite sound position"; return false; }
        return true;
    }

    public static bool Is2D(EnbEffectDefinition definition)
        => EnbRetailIniDocument.ReadBool(definition.Properties,"SFX2D");

    public static string? ValidateDefinition(EnbRetailSoundDefinition d, bool allowSpeech = true, bool allowRateLimiting = false)
    {
        if (d.Files.Count!=1 || d.Files[0].IsNullChoice) return "weighted/NULL sound selection is not implemented";
        if (d.Files[0].Weight is <=0) return "non-positive sound selection weight";
        if (!float.IsFinite(d.EffectiveDopplerTweak) ||
            !float.IsFinite(d.EffectiveMinDopplerShift) || d.EffectiveMinDopplerShift<=0 ||
            !float.IsFinite(d.EffectiveMaxDopplerShift) || d.EffectiveMaxDopplerShift<d.EffectiveMinDopplerShift ||
            !float.IsFinite((d.EffectivePitch+d.EffectiveMaxPitchShift)*d.EffectiveMaxDopplerShift))
            return "invalid effect Doppler limits/tweak";
        if (new[] { d.EffectiveMinVolumeShift, d.EffectiveMaxVolumeShift,
            d.EffectiveMinPitchShift, d.EffectiveMaxPitchShift }.Any(v => !float.IsFinite(v) || v < 0) ||
            d.EffectivePitch - d.EffectiveMinPitchShift <= 0)
            return "invalid sound randomization range";
        if (!float.IsFinite(d.EffectiveSecondsBetweenCalls) || d.EffectiveSecondsBetweenCalls < 0 ||
            (double)d.EffectiveSecondsBetweenCalls * 1000 > int.MaxValue)
            return "invalid sound call interval";
        if (d.EffectiveSecondsBetweenCalls!=0 && !allowRateLimiting) return "sound definition rate limiting is not implemented";
        if (d.EffectiveSoundType!=1 && !(allowSpeech && d.EffectiveSoundType==2)) return "non-SFX sound category is not implemented";
        if (d.EffectiveLoopCount is <0 or >1024) return "sound loop count exceeds bounded output";
        if (d.EffectiveMaxInstances<=0) return "sound definition allows no instances";
        //0043E62E/+78 and0043E640/+198 publish the two radii independently.
        //Installed definitions contain full-volume radii beyond drop-off;
        //neither the factory nor the setters impose an ordering requirement.
        if (!float.IsFinite(d.EffectiveVolume) || d.EffectiveVolume<0 ||
            !float.IsFinite(d.EffectivePitch) || d.EffectivePitch<=0 ||
            !float.IsFinite(d.EffectiveMaxVolumeRadius) || !float.IsFinite(d.EffectiveDropOffRadius) ||
            d.EffectiveDropOffRadius<0)
            return "invalid sound gain/pitch/radii";
        return null;
    }

    // 0043E360/0043E530: volume then pitch, including zero shifts. Preserve
    // the native operation order; rounding base-min before addition differs.
    public static (float Volume, float Pitch) SampleModifiers(EnbRetailSoundDefinition d, float volumeSample, float pitchSample)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        static EnbRetailExtendedX87Register F(float value) => EnbRetailExtendedX87Register.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        static float Sample(float basis, float min, float max, float unit)
            => (float)((F(max) + F(min)) * F(unit) + F(basis) - F(min));
        return (Math.Max(0, Sample(d.EffectiveVolume, d.EffectiveMinVolumeShift, d.EffectiveMaxVolumeShift, volumeSample)),
            Sample(d.EffectivePitch, d.EffectiveMinPitchShift, d.EffectiveMaxPitchShift, pitchSample));
    }
}
