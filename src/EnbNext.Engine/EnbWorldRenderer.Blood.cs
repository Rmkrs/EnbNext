using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<ulong,BloodOwner> _bloodOwners=[];
    private readonly List<(LiveRootInstance Parent,LiveRootInstance Child,int Bone)> _bloodAttachments=[];
    private string _bloodStatus="blood particles inactive";
    private long _bloodSerial;
    private ulong _bloodSoundSerial;
    private readonly List<EnbCurrentRetailEffectSoundNode> _bloodSounds=[];
    private sealed class BloodOwner
    {
        public int Level=-1;
        public ulong Generation;
        public int NextSelection;
        public IReadOnlyList<(int Bone,int Tier)> Selection=[];
        public EnbBloodDefinition? Definition;
        public float Scale;
        public EnbCurrentRetailEffectSoundNode? Loop;
        public readonly Dictionary<long,BloodAttachment> Attachments=[];
    }
    private sealed record BloodAttachment(LiveCpuRoot Root,long Serial,int Bone);

    private void RemoveOwnedBloodChildren(BloodOwner owner,LiveRootInstance parent,int? bone=null)
    {
        var membership=CompositeMembership(parent);
        bool changed=false;
        foreach(var (key,child) in owner.Attachments.ToArray())
        {
            //81189C and811C44 both test the concrete RenderObj name. A
            //nonmatching configured child remains, even on the same pivot.
            if(bone is {} pivot&&child.Bone!=pivot||!IsDamageChildName(child.Root.RuntimeRenderObjectName))continue;
            owner.Attachments.Remove(key);
            if(membership is not null)changed|=membership.Attachments.Remove($"<blood/{parent.Object.RenderIdentity}/{child.Serial}>");
        }
        if(changed)membership!.Rebuild();
    }

    private void PrepareBlood(LiveFrame frame,RootTaskList roots,EnbRetailAssetBundle assets)
    {
        _bloodAttachments.Clear();
        _bloodSounds.Clear();
        int pending=0,blocked=0;string? failure=null;
        var bloodOwners=(_shipExhaustOwner??this)._bloodOwners;
        var live=new HashSet<ulong>();
        foreach(var parent in roots.Where(r=>r.InstancePath=="root").ToArray())
        {
            var obj=parent.Object;
            if(obj.TargetVitals is not {} vitals || obj.ObjectType is not (0 or 25))continue;
            bool organic=obj.ShipPresentation?.IsOrganic==true;
            float? remaining=organic?vitals.ShieldPercent:
                vitals.MaximumHullPoints is >0 && vitals.HullPoints is float hull?hull/vitals.MaximumHullPoints:null;
            int level=EnbBloodCatalog.DamageLevel(remaining);
            live.Add(obj.RenderIdentity);
            if(_shipExhaustOwner is not null && (!bloodOwners.TryGetValue(obj.RenderIdentity,out var shared)||shared.Generation!=obj.VisualGeneration))continue;
            if(!bloodOwners.TryGetValue(obj.RenderIdentity,out var owner)||owner.Generation!=obj.VisualGeneration)
                bloodOwners[obj.RenderIdentity]=owner=new(){Generation=obj.VisualGeneration};
            try
            {
                if(!TryResolveConfiguredAttachmentHierarchy(parent.Root,out var hierarchy,out _)||hierarchy is null||hierarchy.Pivots.Count==0)continue;
                if(_shipExhaustOwner is null&&owner.Level!=level)RemoveOwnedBloodChildren(owner,parent);
                RemoveAuthoredDamageChildren(parent,roots,frame.CurrentClientTime,_shipExhaustOwner is null&&owner.Level!=level);
                if(_shipExhaustOwner is null)
                {
                    if(owner.Level!=level)
                    {
                        //81189C removes old damage children before the new tier's
                        //acquisitions. Their independent buffers drain normally.
                        owner.Loop=null;
                        if(level!=0)
                        {
                            owner.Definition=assets.Blood?.Resolve(obj.BaseAsset,organic)??throw new InvalidDataException("blood.ini unavailable");
                            var hlod=parent.Root.Hlod;
                            if(hlod is null||!TryResolveHlodCompositeBounds(parent,hlod,Matrix4x4.Identity,[],out var bounds,out var error))
                                throw new InvalidDataException("Blood owner bounds unavailable");
                            //811A09 reads parent+F0's radius after the parent's
                            //factory scaling.98D110 returns that stored radius;
                            //the game-object scale must not multiply it again.
                            owner.Scale=Math.Max(.0001f,bounds.WorldSphere.Radius)*owner.Definition.Scale;
                        }
                        owner.Level=level;owner.NextSelection=0;
                        if(level!=0 && owner.Definition!.Sound.Length!=0)
                        {
                            Matrix4x4 soundWorld;
                            if(parent.PrimitiveFactoryWorld is {} factoryWorld)soundWorld=factoryWorld;
                            else if(parent.WorldTransformOverride is {} suppliedWorld)soundWorld=suppliedWorld;
                            else if(!TryBuildObjectWorld(obj,out soundWorld))throw new InvalidDataException("Blood sound world transform unavailable");
                            _bloodSounds.Add(EnbBloodSoundRuntime.CreateReleased(obj,owner.Definition.Sound,
                                ++_bloodSoundSerial,unchecked((uint)frame.CurrentClientTime),soundWorld.Translation));
                        }
                        if(level!=0 && owner.Definition!.LoopingSound.Length!=0)
                            owner.Loop=EnbBloodSoundRuntime.CreateLoop(obj,owner.Definition.LoopingSound,
                                ++_bloodSoundSerial,unchecked((uint)frame.CurrentClientTime));
                        owner.Selection=EnbBloodCatalog.SelectAttachments(obj.GameId,hierarchy.Pivots.Count,level);
                    }
                    for(;owner.NextSelection<owner.Selection.Count;owner.NextSelection++)
                    {
                        var (bone,tier)=owner.Selection[owner.NextSelection];
                        var definition=owner.Definition!;
                        //Construct all occurrences, including ones replaced later
                        //on this same bone before any frame callback can emit.
                        RemoveOwnedBloodChildren(owner,parent,bone);
                        if(!TryCreateBloodFactory(obj,definition.Levels[tier],owner.Scale,definition.Hsv,assets,
                            frame.Snapshot.Environment?.PresentationMode,out var root,out var error,out bool missing))
                        {
                            if(missing)continue;
                            pending++;failure??=error;break;
                        }
                        long serial=++_bloodSerial;
                        owner.Attachments[serial]=new(root,serial,bone);
                    }
                }
                if(owner.Loop is {} loop)_bloodSounds.Add(loop);
                foreach(var attachment in owner.Attachments.Values)
                {
                    int bone=attachment.Bone;
                    var inputs=attachment.Root.ConfiguredFactoryInputs!.Value;
                    var appearance=new FactoryAppearance(inputs.Scale,inputs.Hsv.X,inputs.Hsv.Y,inputs.Hsv.Z);
                    var staged=new RootTaskList();
                    if(!TryAppendRootInstanceTreePrepared(obj,attachment.Root,false,Matrix4x4.Identity,
                        $"blood/{obj.RenderIdentity}/{attachment.Serial}",true,null,staged,out var error,
                        parentInstance:parent,ordinaryHlodPivot:(uint)bone,isAggregateHlodChild:true,appearance:appearance,
                        initialize:instance=>
                        {
                            if(!TryRefreshOrdinaryHlodChild(instance,out var why))throw new InvalidDataException(why);
                        }))throw new InvalidDataException(error);
                    roots.AddRange(staged);
                    _bloodAttachments.Add((parent,staged[0],bone));
                }
            }
            catch(Exception ex){blocked++;failure??=ex.Message;}
        }
        if(_shipExhaustOwner is null)
            foreach(var id in bloodOwners.Keys.Where(id=>!live.Contains(id)).ToArray())bloodOwners.Remove(id);
        _bloodStatus=$"blood particles pending {pending} blocked {blocked}"+(failure is null?"":" · "+failure);
    }

    private void ReportBloodDraws(IReadOnlyList<EnbW3dDrawTask> packets)
    {
        int count=packets.Count(p=>p.RenderInstancePath?.StartsWith("retained-emitter/blood/",StringComparison.Ordinal)==true);
        _bloodStatus=_bloodStatus.Replace("blood particles ",$"blood particles draws {count} ",StringComparison.Ordinal);
    }
}
