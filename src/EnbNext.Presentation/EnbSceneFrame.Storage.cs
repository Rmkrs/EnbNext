using System.Numerics;
namespace EnbNext.Presentation;

public sealed partial class EnbSceneFrame
{
    private EnbSceneSnapshot? _definition;
    private EnbSampledSceneObject[] _objects=[];
    private IEnbRenderObject[]? _singleObject;
    public void SetSingleObject(IEnbRenderObject item){_singleObject??=new IEnbRenderObject[1];_singleObject[0]=item;Objects=_singleObject;}
    private int _leases;
    private EnbSceneFrame? _parent;
    private EnbSceneFrame? _visual;
    public bool IsRetained => Volatile.Read(ref _leases)!=0;
    public EnbSceneFrame Retain()
    {
        _parent?.Retain();
        Interlocked.Increment(ref _leases);
        return this;
    }
    public void Release()
    {
        if(Interlocked.Decrement(ref _leases)<0)
        {Interlocked.Increment(ref _leases);throw new InvalidOperationException("Scene frame lease released twice");}
        _parent?.Release();
    }
    internal void Prepare(EnbSceneSnapshot source)
    {
        if(IsRetained)throw new InvalidOperationException("Cannot overwrite a retained scene frame");
        if(!ReferenceEquals(_definition,source))
        {
            AdoptMetadata(source);_definition=source;
            if(_objects.Length!=source.Objects.Length)
            {
                var previous=_objects;_objects=new EnbSampledSceneObject[source.Objects.Length];
                for(int i=0;i<_objects.Length;i++)_objects[i]=i<previous.Length?previous[i]:new();
                Objects=_objects;
            }
            Objects=_objects;
            for(int i=0;i<_objects.Length;i++)_objects[i].Adopt(source.Objects[i]);
        }
    }
    public void PrepareContext(IEnbSceneFrame source)
    {
        AdoptMetadata(source);
        _parent=source as EnbSceneFrame;
    }
    public void Import(IEnbSceneFrame source)
    {
        AdoptMetadata(source);
        if(_objects.Length!=source.Objects.Count)
        {
            var previous=_objects;_objects=new EnbSampledSceneObject[source.Objects.Count];
            for(int i=0;i<_objects.Length;i++)_objects[i]=i<previous.Length?previous[i]:new();
        }
        for(int i=0;i<_objects.Length;i++)_objects[i].AdoptViewObject(source.Objects[i]);
        CopyCockpit(source);
        Objects=_objects;
    }
    private void CopyCockpit(IEnbSceneFrame source)
    {
        if(source.Hud.Cockpit is {} cockpit)
        {
            _cockpit.EnergyPercent=cockpit.EnergyPercent;_cockpit.MaximumEnergyPower=cockpit.MaximumEnergyPower;
            _cockpit.Vitals=cockpit.Vitals;_cockpit.MaximumSpeed=cockpit.MaximumSpeed;_cockpit.IsAutoFollowing=cockpit.IsAutoFollowing;
            _cockpit.Warp=cockpit.Warp;_cockpit.WarpCommand=cockpit.WarpCommand;Hud.Cockpit=_cockpit;
        }
    }
    public EnbSampledSceneObject MutableObject(int index)=>(EnbSampledSceneObject)Objects[index];
    public EnbSceneFrame PrepareVisualFrame()
    {
        var visual=_visual??=new EnbSceneFrame {_parent=this};
        visual.AdoptMetadata(this);visual.CopyCockpit(this);
        if(visual._objects.Length!=_objects.Length)
        {
            var previous=visual._objects;visual._objects=new EnbSampledSceneObject[_objects.Length];
            for(int i=0;i<visual._objects.Length;i++)visual._objects[i]=i<previous.Length?previous[i]:new();
        }
        for(int i=0;i<_objects.Length;i++)visual._objects[i].AdoptSample(_objects[i]);
        visual.Objects=visual._objects;
        return visual;
    }
    public void ApplyTransform(ulong identity,IEnbRenderTransform transform)
    {
        foreach(var item in Objects)
        {
            if(item.RenderIdentity!=identity || item.Spatial is null)continue;
            var owner=(EnbSampledSceneObject)item;
            owner.Pose=EnbRenderPose.FromTransform(transform);
            RenderPoses.Write(owner);
        }
    }
    private readonly EnbCockpitFrame _cockpit=new();
    private readonly EnbVitalsFrame _vitals=new();
    private EnbNext.Net7.World.Net7AuxDataSchema? _warpAux;
    private ulong _warpRevision;
    private int? _warpMode;
    internal void SampleCockpit(EnbNext.Net7.World.Net7WorldObject? ship,uint time,int? mode)
    {
        if(!EnbPresentationSnapshotBuilder.SampleCockpit(ship,time,_cockpit,_vitals))
        {Hud.Cockpit=null;_warpAux=null;_cockpit.Warp=null;return;}
        var data=ship!.AuxData!;
        if(!ReferenceEquals(_warpAux,data) || _warpRevision!=data.Root.MaterializedRevision || _warpMode!=mode)
        {
            _cockpit.Warp=EnbNext.Net7.Gameplay.Net7WarpStatus.Read(data,mode);
            _warpAux=data;_warpRevision=data.Root.MaterializedRevision;_warpMode=mode;
        }
        _cockpit.WarpCommand=null;Hud.Cockpit=_cockpit;
    }
    internal EnbSampledSceneObject SampleObject(int index)=>_objects[index];
}

/// <summary>Actual copied network-sample fields owned by a reusable frame.
/// It neither inherits a snapshot nor reads a mutable source through getters.</summary>
public sealed class EnbSampledSceneObject : EnbRenderObjectState
{
    private EnbSceneObjectSnapshot? _definition;
    private readonly SampledSpatial _spatial=new();
    internal void Adopt(EnbSceneObjectSnapshot source)
    {
        if(ReferenceEquals(_definition,source))return;
        _definition=source;AdoptDefinition(source);
        if(source.Spatial is {} spatial){_spatial.Adopt(spatial);Spatial=_spatial;}
    }
    internal void AdoptSample(EnbSampledSceneObject source)
    {
        if(source._definition is {} definition)Adopt(definition);else AdoptDefinition(source);
        if(source.Spatial is not null){_spatial.Adopt(source.Spatial);Spatial=_spatial;}
        ShipPresentation=source.ShipPresentation;SpeakingColor=source.SpeakingColor;
        ShipVisualBankRadians=source.ShipVisualBankRadians;ShipEmote=source.ShipEmote;RenderPoseSlot=source.RenderPoseSlot;
    }
    public void AdoptViewObject(IEnbRenderObject source)
    {
        AdoptDefinition(source);
        if(source.Spatial is {} spatial){_spatial.Adopt(spatial);Spatial=_spatial;}
    }
    public void SetFixedPose(EnbRenderPose pose){_spatial.SetFixed();_spatial.Pose.Value=pose;Spatial=_spatial;}
    public EnbRenderPose Pose {get=>_spatial.Pose.Value;set=>_spatial.Pose.Value=value;}
    internal void Sample(EnbRenderPose pose,int slot)
    {
        RenderPoseSlot=slot;
        ShipPresentation=_definition?.ShipPresentation;SpeakingColor=_definition?.SpeakingColor;
        ShipVisualBankRadians=_definition?.ShipVisualBankRadians;ShipEmote=_definition?.ShipEmote;
        if(_definition?.Spatial is {} source)
        {
            _spatial.ModelAudit=source.ModelAudit;_spatial.PacketTime=source.PacketTime;_spatial.ReceiptTime=source.ReceiptTime;
            _spatial.Pose.Value=pose.HasSample?pose:EnbRenderPose.FromTransform(source.Transform);
        }
    }
    internal void SetFormationAudit(string audit,uint? packet,uint? receipt)
    {_spatial.ModelAudit=audit;_spatial.PacketTime=packet;_spatial.ReceiptTime=receipt;}
    private sealed class SampledSpatial : IEnbRenderSpatial
    {
        public readonly SampledTransform Pose=new();
        public IEnbRenderTransform Transform=>Pose;
        public EnbSceneSpatialProviderKind ProviderKind {get;private set;}
        public EnbScenePlanetSnapshot? Planet {get;private set;}
        public EnbSceneAdvancedSpatialSnapshot? Advanced {get;private set;}
        public uint? FormationLeaderId {get;private set;}
        public string ModelAudit {get;set;}="";
        public uint? PacketTime {get;set;}
        public uint? ReceiptTime {get;set;}
        public uint TransitionMilliseconds {get;private set;}
        public void SetFixed(){ProviderKind=EnbSceneSpatialProviderKind.Fixed;Planet=null;Advanced=null;FormationLeaderId=null;ModelAudit="";PacketTime=null;ReceiptTime=null;TransitionMilliseconds=0;}
        public void Adopt(IEnbRenderSpatial source)
        {
            ProviderKind=source.ProviderKind;Planet=source.Planet;Advanced=source.Advanced;
            FormationLeaderId=source.FormationLeaderId;ModelAudit=source.ModelAudit;
            PacketTime=source.PacketTime;ReceiptTime=source.ReceiptTime;TransitionMilliseconds=source.TransitionMilliseconds;
            Pose.Value=EnbRenderPose.FromTransform(source.Transform);
        }
    }
    private sealed class SampledTransform : IEnbRenderTransform
    {
        public EnbRenderPose Value;
        public uint ClientTime=>Value.Sample.ClientTime;
        public Vector3 Position=>Value.Sample.Position;
        public Quaternion Orientation=>Value.Sample.Orientation;
        public Vector3 Velocity=>Value.Sample.Velocity;
        public float Speed=>Value.Sample.Speed;
        public Vector3 Rotation=>Value.Sample.Rotation;
        public Vector3 OrientationColumn0=>Value.Sample.OrientationColumn0;
        public Matrix4x4? ProviderTransform=>Value.Sample.ProviderTransform;
        public float BankHeadingTarget=>Value.Sample.BankHeadingTarget;
    }
}
