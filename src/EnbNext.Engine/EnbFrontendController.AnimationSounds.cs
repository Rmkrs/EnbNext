using EnbNext.Presentation;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbFrontendController
{
    // CharacterView +68 owns the listener (679930 -> 43FA20), independently
    // of the narrator and ShipPIP cameras. Removing a scene does not reset it.
    private EnbResolvedRenderCamera? _animationSoundListener;

    private void CollectAnimationSounds(LiveAvatarSpeechPlayback? playback,
        EnbAvatarPortraitAsset pose, Matrix4x4 world)
    {
        if(playback is null)return;
        foreach(var cue in playback.AnimationSounds)
            _gpu.QueueClientAnimationSound(cue.Sound.Sound,cue.Sound.Flat,
                Vector3.Transform(pose.AnimationSoundPosition!(cue.Bone),world));
    }

    private void SetAnimationSoundListener(EnbFrontendModel model,
        EnbW3dHierarchyPoseOwner pose,EnbRenderViewport viewport)
    {
        Matrix4x4.Decompose(pose.WorldTransforms[model.Pivot("CAMERA")],out _,out var rotation,out var position);
        if(EnbResolvedRenderCamera.TryCreate(position,rotation,viewport,Math.PI/4,4d/3,new(1,1000),out var camera))
            _animationSoundListener=camera;
    }
}
