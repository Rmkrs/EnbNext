using EnbNext.Rendering;
using EnbNext.Assets;
using EnbNext.Presentation;
using System.Numerics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbBridgeRenderFrame? _nextBridgeFrame;
    private EnbRetailBridgeResource? _bridgeGpuAsset;
    private readonly List<(LiveLanePlan Lane, LiveGpuResource Gpu)> _bridgeGpu = [];
    private EnbW3dLightEnvironment _bridgeLights = EnbW3dLightEnvironment.BlackNoLights;
    private string _bridgeStatus = "Bridge inactive";
    private EnbSceneAvatarSnapshot? _bridgeAvatarIdentity;

    private readonly List<(LiveLanePlan Lane, LiveGpuResource Gpu)> _bridgeAvatarGpu = [];


    private readonly EnbBridgeAvatarSteering _bridgeAvatarSteering = new();
    private string _bridgeAvatarTag = "breathe1";

    private EnbAvatarPortraitAsset? _bridgeAvatarPose;


    private LiveAvatarSpeechPlayback? _bridgeAvatarPlayback;
    private long _bridgeAvatarAppliedVersion;
    private readonly List<EnbCurrentRetailEffectSoundNode> _bridgeAnimationSounds=[];

    private ulong _bridgeAvatarWarpSequence;
    private ulong? _bridgeReactionOwner;
    private EnbNext.Net7.Gameplay.Net7IncapacitationDetails? _bridgeRecoverySeen;
    public string BridgeStatus => _bridgeStatus;

    public void SetBridgeFrame(EnbBridgeRenderFrame? frame) => _nextBridgeFrame = frame;

    private void DestroyBridgeResources(EnbGlApi? gl)
    {
        foreach (var draw in _bridgeGpu) draw.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _bridgeGpu.Clear();
        foreach (var draw in _bridgeAvatarGpu) draw.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _bridgeAvatarGpu.Clear(); _bridgeAvatarIdentity = null;
        _bridgeAvatarSteering.Reset(); _bridgeAvatarTag = "breathe1";
        _bridgeAvatarPose = null;
        _bridgeAvatarPlayback=null;
        _bridgeGpuAsset = null;
        _bridgeStatus = "Bridge inactive";
    }

    private void RenderBridge(EnbGlApi gl, int fb, int width, int height, LiveFrame frame)
    {
        IEnbRenderObject? owner = _ownedSceneFrame?_localSceneOwner:
            SnapshotObjects(frame.Snapshot).FirstOrDefault(o => o.IsLocalShip);
        if (_bridgeReactionOwner != owner?.RenderIdentity)
        {
            _bridgeGadgetSequence = Interlocked.Read(ref _gadgetFlashSequence);
            _bridgeReactionOwner = owner?.RenderIdentity; _bridgeRecoverySeen = null;
            var changes = owner?.ShipPresentation?.PrivateWarpChanges ?? [];
            _bridgeAvatarWarpSequence = changes.Length > 1 ? changes[^2].Sequence : 0;
        }
        if (frame.Bridge is not {} bridge)
        {
            _bridgeGadgetSequence = Interlocked.Read(ref _gadgetFlashSequence);
            // Retire transient events even while the bridge is hidden. Reopening
            // first person must not replay an old warp or incapacitation callback.
            _bridgeAvatarWarpSequence = owner?.ShipPresentation?.PrivateWarpChanges.LastOrDefault()?.Sequence ?? _bridgeAvatarWarpSequence;
            _bridgeRecoverySeen = frame.Snapshot.IncapacitationDetails;
            if (_bridgeGpuAsset is not null) DestroyBridgeResources(gl);
            return;
        }
        if (owner is null) throw new InvalidDataException("Bridge frame has no owner.");
        var resource = bridge.Asset.Resource;
        var hlod = resource.HierarchicalLods.SingleOrDefault();
        EnbW3dHlodSelection? selection = hlod is null ? null : new(hlod.Name, hlod.Levels.Single().Ordinal);
        var camera = EnbBridgeCameraComposition.ResolveCamera(bridge.Cameras.BridgeCamera,
            frame.Camera, EnbBridgeCameraComposition.ClipRange);
        var lights = EnbW3dLightEnvironment.CreateCurrentRetail(_bridgeLights.GlobalAmbientRgb24,
            _bridgeLights.Lights.Select(slot => slot with { Light = slot.Light with {
                Direction = bridge.Cameras.TransformWorldLightDirection(slot.Light.Direction) } }));
        if (!EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner, resource, bridge.ModelTransform,
            frame.CurrentClientTime, EnbRenderPassKind.OrdinaryWorld, selection, lights, bridge.Animation,
            out var draws, out var error) ||
            !EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.OrdinaryWorld,
                draws.Packets.Where(p => (p.Mesh.Attributes & 0x1000) == 0).ToArray(), out var order, out error))
            throw new InvalidDataException("Bridge draw preparation: " + error);
        var specialPackets=order.Packets.Where(p=>p.SpecialMaterial is not null).ToArray();
        order=order with {Packets=[..order.Packets.Where(p=>p.SpecialMaterial is null)]};
        try
        {
            if (!ReferenceEquals(_bridgeGpuAsset, bridge.Asset)) DestroyBridgeResources(gl);
            if (_bridgeGpuAsset is null)
            {
                var textures = bridge.Asset.FailedLoadTextures;
                foreach (var packet in order.Packets)
                {
                    if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet, out var submission, out error) ||
                        !TryCompileLane(submission, camera, EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault, _retailAssetsAccessor()!.Textures,
                            textures, null, LiveLaneKind.Textured, out var lane, out error) ||
                        !TryCreateGpuResource(gl, lane, LiveLaneKind.Textured, 0, out var gpu, out error))
                        throw new InvalidDataException("Bridge GPU: " + error);
                    _bridgeGpu.Add((lane, gpu));
                }
                _bridgeGpuAsset = bridge.Asset;
            }
            if (order.Packets.Length != _bridgeGpu.Count) throw new InvalidDataException("Bridge draw topology changed.");
            for (int i = 0; i < _bridgeGpu.Count; i++)
            {
                var draw = _bridgeGpu[i];
                var previous = draw.Lane.Textured!;
                if (!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(previous,
                    previous.Submission with { Packet = order.Packets[i] }, camera, out var updated, out error))
                    throw new InvalidDataException("Bridge frame: " + error);
                _bridgeGpu[i] = (LiveLanePlan.From(updated), draw.Gpu);
            }
            _setOrbitalDepthRange?.Invoke(EnbBridgeCameraComposition.MinimumDepth, EnbBridgeCameraComposition.MaximumDepth);
            // 0050D78E: clear depth, preserve world color before drawing bridge+120.
            gl.DepthMask(1);
            gl.Clear(EnbGlConstants.GL_DEPTH_BUFFER_BIT);
            SubmitSpecialScene(gl,fb,width,height,_bridgeGpu,specialPackets,camera,lights,
                _bridgeSpecialAmbient,frame.CurrentClientTime);
            if (owner.Avatar is {} avatar)
            {
                // 0053B7A0 copies appearance, replacing only Personality with 8/9.
                var identity = avatar with { Personality = (byte)(avatar.Gender == 0 ? 8 : 9) };
                if (!SamePortrait(_bridgeAvatarIdentity, identity))
                {
                    foreach (var draw in _bridgeAvatarGpu) draw.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
                    _bridgeAvatarGpu.Clear(); _bridgeAvatarIdentity = identity;


                    _bridgeAvatarSteering.Reset(); _bridgeAvatarTag = "breathe1";
                    _bridgeAvatarPose = null;
                    _bridgeAvatarAppliedVersion=_bridgeAvatarSteering.Version;
                    // 0053B7A0 retains default head/morph/idle flags.
                    _bridgeAvatarPlayback=new(_retailAssetsAccessor()!,identity,portraitIdle:true);
                    _bridgeAvatarPlayback.StartIdle(frame.CurrentClientTime*.001);
                }
                var assets = _retailAssetsAccessor()!;
                if (!EnbCurrentRetailEnvironmentMapRuntime.TryResolveReflection(assets.EnvironmentMaps,
                    frame.Snapshot.Environment, out var reflection, out var reflectionError))
                    throw new InvalidDataException(reflectionError);
                long gadgetSequence = Interlocked.Read(ref _gadgetFlashSequence);
                if (gadgetSequence != _bridgeGadgetSequence)
                {
                    _bridgeGadgetSequence = gadgetSequence;
                    _bridgeAvatarSteering.Request(EnbBridgeAvatarReaction.UseControls);
                }
                foreach (var change in owner.ShipPresentation?.PrivateWarpChanges ?? [])
                {
                    if (change.Sequence <= _bridgeAvatarWarpSequence) continue;
                    _bridgeAvatarWarpSequence = change.Sequence;
                    EnbBridgeAvatarReaction? reaction = change.State switch { 2 => EnbBridgeAvatarReaction.StartWarp,
                        3 or 6 or 7 or 8 or 9 or 11 => EnbBridgeAvatarReaction.StopHard, _ => null };
                    if (reaction is {} value) _bridgeAvatarSteering.Request(value);
                }
                if (!ReferenceEquals(_bridgeRecoverySeen, frame.Snapshot.IncapacitationDetails))
                {
                    _bridgeRecoverySeen = frame.Snapshot.IncapacitationDetails;
                    // IncapacitanceDetailsPacket 00739BB0 -> 00584690 -> 0053B770.
                    if (_bridgeRecoverySeen is not null) _bridgeAvatarSteering.Request(EnbBridgeAvatarReaction.LoseControl, 1);
                }
                // 0053B670: only nonzero screen-plane shake requests get_hit.
                // Camera-depth normalization does not change this zero test.
                if (_frameShake.X != 0 || _frameShake.Y != 0)
                    _bridgeAvatarSteering.Request(EnbBridgeAvatarReaction.GetHit);
                void ApplyControl()
                {
                    if(_bridgeAvatarSteering.Tag is not {} command)return;
                    if(_bridgeAvatarAppliedVersion!=_bridgeAvatarSteering.Version)
                    {
                        _bridgeAvatarAppliedVersion=_bridgeAvatarSteering.Version;
                        _bridgeAvatarPlayback!.BindControlledBody(command,_bridgeAvatarSteering.Target,_bridgeAvatarSteering.Flags);
                    }
                    else _bridgeAvatarPlayback!.SetControlledBodyTarget(_bridgeAvatarSteering.Target,_bridgeAvatarSteering.Flags);
                }
                ApplyControl(); // externally requested reactions precede the actor update
                var posed=_bridgeAvatarPlayback!.Evaluate(frame.CurrentClientTime*.001);
                // 53CB40 updates the actor first, observes its controlled slot,
                // then sets next-frame steering targets or binds a new turn.
                _bridgeAvatarSteering.ObserveControlledBody(_bridgeAvatarPlayback.ControlledBodyActive);
                _bridgeAvatarSteering.SetSteering(bridge.AvatarSteeringRate);
                ApplyControl();
                _bridgeAvatarTag=_bridgeAvatarSteering.Tag??"breathe1";
                if(posed is null)return;
                _bridgeAvatarPose=posed;
                var actorWorld=bridge.Asset.AuthoredTransform("MAIN_CHAIR",bridge.Animation)*bridge.ModelTransform;
                RenderStationPerson(gl, fb, width, height, posed, identity,
                    actorWorld, camera,
                    frame.CurrentClientTime, _bridgeAvatarGpu, lights, reflection);
                foreach(var cue in _bridgeAvatarPlayback.AnimationSounds)
                {
                    var position=Vector3.Transform(posed.AnimationSoundPosition!(cue.Bone),actorWorld);
                    var node=EnbWarpSoundRuntime.Node(owner,++_shipAnimationSoundSequence,0,cue.Sound.Sound,cue.Sound.Flat);
                    _bridgeAnimationSounds.Add(node with {ClientWarp=false,ClientAnimation=true,BoundGameId=null,
                        ReleasedWorldPosition=position,ActualStartTick=unchecked((uint)frame.CurrentClientTime),
                        Root=node.Root with {GeometryKind=EnbSceneEffectGeometryKind.Point,WorldPoint=position}});
                }
            }
            _bridgeStatus = $"{bridge.Asset.Name}: {_bridgeGpu.Count} bridge GPU draws";
        }
        catch
        {
            DestroyBridgeResources(gl);
            throw;
        }
        finally { _setOrbitalDepthRange?.Invoke(0, 1); }
    }
}

