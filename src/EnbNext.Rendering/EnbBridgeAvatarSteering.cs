using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>0053CB40 steering commands and 00783620 bounded animation playback.</summary>
public sealed class EnbBridgeAvatarSteering
{
    public string? Tag { get; private set; }
    public long Version {get;private set;}
    public float Target { get; private set; }
    public bool Returning { get; private set; }
    public EnbBridgeAvatarReaction Reaction { get; private set; } = EnbBridgeAvatarReaction.Idle;

    public void Reset() { Tag = null; Target = 0; Returning = false; Reaction = EnbBridgeAvatarReaction.Idle; }
    public uint Flags=>Reaction==EnbBridgeAvatarReaction.Steering && !Returning?2u:0u;
    // 53CB40 observes 789D40 after the avatar update. Completion belongs to
    // the retained slot's control flags, not a second duration clock.
    public void ObserveControlledBody(bool active){if(!active)Reset();}

    public bool Request(EnbBridgeAvatarReaction reaction,Func<int,int,int>? random=null)
    {
        // 0053CD60 rejects repeated/lower-priority commands before drawing.
        // Warp, stop and loss-of-control jump past variant selection entirely.
        if(reaction==Reaction || ((int)reaction>>16)>((int)Reaction>>16))return false;
        int variant=reaction is EnbBridgeAvatarReaction.GetHit or EnbBridgeAvatarReaction.UseControls
            ? (random??EnbRetailPresentationRandom.Shared.Next)(1,4):1;
        return Request(reaction,variant);
    }

    public bool Request(EnbBridgeAvatarReaction reaction, int variant)
    {
        if (reaction is EnbBridgeAvatarReaction.Idle or EnbBridgeAvatarReaction.Steering || variant is < 1 or > 4)
            throw new ArgumentOutOfRangeException();
        if (reaction == Reaction || ((int)reaction >> 16) > ((int)Reaction >> 16)) return false;
        string prefix = reaction switch { EnbBridgeAvatarReaction.LoseControl => "lose_control",
            EnbBridgeAvatarReaction.StopHard => "stop_hard", EnbBridgeAvatarReaction.StartWarp => "start_warp",
            EnbBridgeAvatarReaction.GetHit => "get_hit", EnbBridgeAvatarReaction.UseControls => "use_controls",
            _ => throw new ArgumentOutOfRangeException() };
        // 0053CD60 jumps past suffix formatting for warp/stop/loss-of-control.
        Tag = reaction is EnbBridgeAvatarReaction.GetHit or EnbBridgeAvatarReaction.UseControls ? prefix + variant : prefix;
        Reaction = reaction; Version++; Target = 1; Returning = false;
        return true;
    }

    public void SetSteering(Vector3 previousSmoothedRate)
    {
        if (Reaction < EnbBridgeAvatarReaction.Steering) return;
        // +64/+68/+6C are X/Y/Z. Retail updates the avatar before model smoothing.
        string? next = previousSmoothedRate.Z > .0001f ? "left_turn_small"
            : previousSmoothedRate.Z < -.0001f ? "right_turn_small" : null;
        if (next is null) { if (Reaction == EnbBridgeAvatarReaction.Steering) { Target = 0; Returning = true; } return; }
        if (Tag != next)
        {
            Tag = next; Reaction = EnbBridgeAvatarReaction.Steering; Version++; Target = 0; Returning = false;
            return; // A new clip first receives target zero with hold flag 2.
        }
        Target = Math.Clamp(Math.Max(0, previousSmoothedRate.Y * .5f + Math.Abs(previousSmoothedRate.Z)) * 5.7295775f, 0, 1);
        Returning = false;
    }
}

// Retail compares high words: stop/warp share priority, and may replace each other.
public enum EnbBridgeAvatarReaction { LoseControl = 0x10000, StopHard = 0x20000, StartWarp = 0x20001,
    GetHit = 0x30000, Steering = 0x40000, UseControls = 0x50000, Idle = 0x60000 }
