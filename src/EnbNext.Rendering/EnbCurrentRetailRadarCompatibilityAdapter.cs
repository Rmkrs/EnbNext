using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public readonly record struct EnbRadarRelativePosition(
    float Forward,
    float Right,
    float Up);

public sealed record EnbRadarContact(
    ulong RenderIdentity,
    uint GameId,
    byte ObjectType,
    string? Name,
    Vector3 WorldPosition,
    float WorldRadius,
    string IconAssetName,
    EnbRadarRelativePosition RelativePosition,
    float CenterDistance,
    float SurfaceDistance,
    float TargetableRange,
    uint RelationshipRaw,
    bool IsAttacking,
    bool IsGroupMember,
    bool IsPvpAttackable,
    bool IsCurrentTarget,
    string? GuildTag,
    Vector3? GuildTagColor,
    float? ShieldPercent)
{
    public bool IsNavigation { get; init; }
    public bool IsRescueBeaconActive { get; init; }
    public EnbNext.Assets.EnbRgb? SpeakingColor { get; init; }
    public bool IsLocalShip { get; init; }
    /// <summary>The level the server sent: a ship's combat level (0 for other players is left out), otherwise a
    /// resource's tech level from its Harvestable aux data (the scanner's "Tech Level" line). Null while unknown.</summary>
    public int? Level { get; init; }
    /// <summary>Speed in the units the speed gauge shows (velocity per millisecond times 1000); null when not finite.</summary>
    public float? Speed { get; init; }
}

public sealed record EnbRadarFrame(
    bool IsAvailable,
    int? ScanRange,
    ulong ObserverRenderIdentity,
    float ObserverWorldRadius,
    ImmutableArray<EnbRadarContact> Contacts)
{
    public static EnbRadarFrame Unavailable(int? scanRange = null)
        => new(false, scanRange, 0, 0f, ImmutableArray<EnbRadarContact>.Empty);
}

/// <summary>
/// Converts immutable scene objects into current-retail radar contacts. The
/// eligibility list, targetability ranges, and per-type gutter icon identities are the behavior of
/// CanClientObjectBeTargeted (client.exe 0x006F5980) and
/// RadarObjectContextIsTargetable (0x007BE3A0), plus
/// GutterRadarGetIconAssetNameForObjectType (0x007B6A00). Presentation/layout remains
/// separate from these compatibility decisions.
/// </summary>
public static class EnbCurrentRetailRadarCompatibilityAdapter
{
    private const byte DecorationObjectType = 0x25;

    public static EnbRadarFrame Build(IEnbSceneFrame snapshot, IReadOnlyDictionary<ulong, float>? distanceRadii = null,bool includeLocal=false,bool retainSelected=false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IEnbRenderObject? observer = snapshot.Objects.FirstOrDefault(static obj => obj.IsLocalShip);
        uint? selected = snapshot.Hud.HasLocalTargetSelection
            ? snapshot.Hud.LocalSelectedTargetId
            : snapshot.Hud.CurrentTargetId;
        EnbRadarFrame frame = Build(observer, snapshot.Objects, snapshot.Hud.ScanRange, snapshot.Hud.GroupMemberIds, distanceRadii,includeLocal,retainSelected?selected:null);
        return selected is uint id
            ? frame with
            {
                Contacts = frame.Contacts
                    .Select(contact => contact with { IsCurrentTarget = contact.GameId == id })
                    .ToImmutableArray(),
            }
            : frame;
    }

    public static EnbRadarFrame Build(
        IEnbRenderObject? observer,
        IReadOnlyList<IEnbRenderObject> objects,
        int? scanRange,
        IReadOnlyCollection<uint>? groupMemberIds = null,
        IReadOnlyDictionary<ulong, float>? distanceRadii = null,bool includeLocal=false,uint? retainedTargetId=null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (observer?.Spatial?.Transform is not { } observerTransform ||
            !IsFinite(observerTransform.Position) ||
            !EnbRenderCameraBasis.TryCreate(observerTransform.Orientation, out EnbRenderCameraBasis basis))
        {
            return EnbRadarFrame.Unavailable(scanRange);
        }

        float observerRadius = ResolveRadiusOverride(observer.RenderIdentity, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(observer), distanceRadii);
        var contacts = ImmutableArray.CreateBuilder<EnbRadarContact>();
        foreach (IEnbRenderObject obj in objects)
        {
            // 7BCA70: current target bypasses the gutter's self/range gates,
            // after the finite-position and 7BE350 object-eligibility checks.
            bool retainedTarget=obj.GameId==retainedTargetId;
            if ((!includeLocal && !retainedTarget && (ReferenceEquals(obj, observer) || obj.IsLocalShip)) ||
                obj.Spatial?.Transform is not { } objectTransform ||
                !IsFinite(objectTransform.Position) ||
                !CanClientObjectBeTargeted(obj) ||
                (!retainedTarget && !IsTargetable(observer, obj, scanRange, distanceRadii)))
            {
                continue;
            }

            Vector3 delta = objectTransform.Position - observerTransform.Position;
            float centerDistance = delta.Length();
            if (!float.IsFinite(centerDistance))
                continue;

            float objectRadius = ResolveRadiusOverride(obj.RenderIdentity, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj), distanceRadii);
            float surfaceDistance = MathF.Max(0f, centerDistance - observerRadius - objectRadius);
            float targetableRange = ResolveTargetableRange(obj, scanRange);
            bool isGroupMember = obj.ObjectType == 0x01 && groupMemberIds?.Contains(obj.GameId) == true;
            bool isPvpAttackable = obj.ObjectType == 0x01 &&
                !isGroupMember &&
                observer.PlayerPresentation?.IsInPvp == true &&
                obj.PlayerPresentation?.IsInPvp == true;
            contacts.Add(new EnbRadarContact(
                obj.RenderIdentity,
                obj.GameId,
                obj.ObjectType,
                ResolveContactName(obj),
                objectTransform.Position,
                objectRadius,
                ResolveIconAssetName(obj.ObjectType),
                new EnbRadarRelativePosition(
                    Vector3.Dot(delta, basis.Forward),
                    Vector3.Dot(delta, basis.Right),
                    Vector3.Dot(delta, basis.Up)),
                centerDistance,
                surfaceDistance,
                targetableRange,
                obj.RelationshipRaw,
                obj.IsAttacking,
                isGroupMember,
                isPvpAttackable,
                obj.IsCurrentTarget,
                ResolveGuildTag(observer, obj),
                ResolveGuildTagColor(observer, obj),
                obj.PlayerPresentation?.ShieldPercent)
            {
                IsNavigation = obj.Radar.Navigation is not null,
                IsRescueBeaconActive = obj.IsRescueBeaconActive,
                SpeakingColor = obj.SpeakingColor,
                IsLocalShip = ReferenceEquals(obj,observer) || obj.IsLocalShip,
                Level = obj.PlayerPresentation?.CombatLevel is int level && level > 0 ? level : obj.HarvestableTechLevel is int tech && tech > 0 ? tech : null,
                Speed = objectTransform.Velocity.Length() * 1000 is var speed && float.IsFinite(speed) ? speed : null,
            });
        }

        ImmutableArray<EnbRadarContact> ordered = contacts
            .ToImmutable()
            .OrderBy(static contact => contact.CenterDistance)
            .ThenBy(static contact => contact.RenderIdentity)
            .ToImmutableArray();
        return new EnbRadarFrame(true, scanRange, observer.RenderIdentity, observerRadius, ordered);
    }

    /// <summary>
    /// Applies RenderObj attachment-controller radii after the asset-backed
    /// renderer has materialized them. Current retail's distance path reads
    /// ClientGameObject+0x9C after that same controller refresh.
    /// </summary>
    public static EnbRadarFrame ApplyDistanceRadiusOverrides(
        EnbRadarFrame frame,
        IReadOnlyDictionary<ulong, float> radii)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(radii);
        if (!frame.IsAvailable)
            return frame;

        float observerRadius = ResolveRadiusOverride(
            frame.ObserverRenderIdentity,
            frame.ObserverWorldRadius,
            radii);
        ImmutableArray<EnbRadarContact> contacts = frame.Contacts
            .Select(contact =>
            {
                float objectRadius = ResolveRadiusOverride(contact.RenderIdentity, contact.WorldRadius, radii);
                return contact with
                {
                    WorldRadius = objectRadius,
                    SurfaceDistance = MathF.Max(0f, contact.CenterDistance - observerRadius - objectRadius),
                };
            })
            .ToImmutableArray();
        return frame with
        {
            ObserverWorldRadius = observerRadius,
            Contacts = contacts,
        };
    }

    /// <summary>
    /// Exact gutter texture identities returned by current retail's object-type
    /// switch. These are loaded onto the shared 12x12 gutter_icon.w3d quad.
    /// The installed resources retain retail's historical <c>g_misile</c> and
    /// <c>g_resrce</c> spellings.
    /// </summary>
    public static string ResolveIconAssetName(byte objectType)
        => objectType switch
        {
            0x00 => "g_npc.tga",
            0x01 => "g_pc.tga",
            0x02 => "g_cap.tga",
            0x03 => "g_planet.tga",
            0x04 => "g_item.tga",
            0x09 => "g_misile.tga",
            0x0A => "g_nebula.tga",
            0x0B => "g_gate.tga",
            0x0C => "g_sbase.tga",
            0x18 => "g_build.tga",
            0x1B => "g_mine.tga",
            0x22 or 0x26 => "g_resrce.tga",
            0x23 => "g_drone.tga",
            0x25 => "g_deco.tga",
            _ => "g_dead.tga",
        };

    public static bool CanClientObjectBeTargeted(IEnbRenderObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj.ObjectType switch
        {
            0x00 or 0x01 or 0x02 or 0x03 or 0x04 or
            0x0B or 0x0C or 0x18 or 0x19 or 0x22 or
            0x23 or 0x26 => true,
            DecorationObjectType => obj.Radar.AppearsInRadar,
            _ => false,
        };
    }

    public static bool IsTargetable(
        IEnbRenderObject? observer,
        IEnbRenderObject obj,
        int? scanRange,
        IReadOnlyDictionary<ulong, float>? distanceRadii = null,float? observerRadius=null,float? objectRadius=null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (!CanClientObjectBeTargeted(obj) ||
            observer?.Spatial?.Transform is not { } observerTransform ||
            obj.Spatial?.Transform is not { } objectTransform ||
            !IsFinite(observerTransform.Position) ||
            !IsFinite(objectTransform.Position))
        {
            return false;
        }

        EnbSceneNavigationSnapshot? navigation = obj.Radar.Navigation;
        if (obj.ObjectType != DecorationObjectType &&
            (navigation is null || navigation.IsHuge))
        {
            return true;
        }

        float targetRange = ResolveTargetableRange(obj, scanRange);
        if (targetRange == float.MaxValue)
            return true;

        return EnbCurrentRetailRadarRenderRangeRuntime.IsWithinTargetableRange(
            observerTransform.Position,
            ResolveRadiusOverride(observer.RenderIdentity, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(observer), distanceRadii,observerRadius),
            objectTransform.Position,
            ResolveRadiusOverride(obj.RenderIdentity, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj), distanceRadii,objectRadius),
            targetRange);
    }

    private static float ResolveTargetableRange(IEnbRenderObject obj, int? scanRange)
    {
        if (obj.ObjectType == DecorationObjectType)
            return obj.Radar.RadarRange;

        EnbSceneNavigationSnapshot? navigation = obj.Radar.Navigation;
        return navigation is null || navigation.IsHuge
            ? float.MaxValue
            : navigation.Signature + scanRange.GetValueOrDefault();
    }

    public static string? ResolveContactName(IEnbRenderObject obj)
        => EnbRetailObjectDisplayName.Resolve(obj);

    private static string? ResolveGuildTag(IEnbRenderObject observer, IEnbRenderObject obj)
    {
        if (obj.ObjectType != 0x01 || obj.PlayerPresentation is not { } player ||
            player.GuildRank == -1 || string.IsNullOrWhiteSpace(player.GuildName))
            return null;

        bool sameGuild = IsSameGuild(observer, obj);
        string rank = sameGuild ? player.GuildRankName?.Trim() ?? string.Empty : string.Empty;
        return rank.Length == 0
            ? $"[{player.GuildName}]"
            : $"[{player.GuildName} {rank}]";
    }

    private static Vector3? ResolveGuildTagColor(IEnbRenderObject observer, IEnbRenderObject obj)
        => obj.ObjectType == 0x01 && obj.PlayerPresentation is { } player
            ? IsSameGuild(observer, obj) ? player.SameGuildTagColor : player.OtherGuildTagColor
            : null;

    private static bool IsSameGuild(IEnbRenderObject observer, IEnbRenderObject obj)
        => !string.IsNullOrWhiteSpace(observer.PlayerPresentation?.GuildName) &&
           !string.IsNullOrWhiteSpace(obj.PlayerPresentation?.GuildName) &&
           string.Equals(
               observer.PlayerPresentation.GuildName,
               obj.PlayerPresentation.GuildName,
               StringComparison.Ordinal);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float ResolveRadiusOverride(
        ulong renderIdentity,
        float fallback,
        IReadOnlyDictionary<ulong, float>? radii,float? explicitRadius=null)
        => explicitRadius is {} supplied ? float.IsFinite(supplied)&&supplied>=0f?supplied:fallback : radii is not null && radii.TryGetValue(renderIdentity, out float radius) &&
           float.IsFinite(radius) &&
           radius >= 0f
            ? radius
            : fallback;
}
