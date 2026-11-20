using System.Collections.Immutable;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using static EnbNext.Presentation.EnbSceneInventorySnapshotBuilder;

namespace EnbNext.Presentation;

// Owned by the presentation builder, used only under the network mutation lock.
// Published items contain detached values, never these retained source bindings.
public sealed class EnbInventoryPublicationOwner
{
    private readonly CollectionOwner[] _collections = new CollectionOwner[19];
    internal ImmutableArray<EnbSceneItemSnapshot> Sample(Net7AuxDataProperty? source, byte inventory,
        bool ammo, Net7ItemTemplateCatalog? templates)
    {
        int index=ammo?0:inventory;
        var collection=_collections[index]??=new();
        return collection.Sample(source,inventory,ammo,templates);
    }
    internal void ClearTrade()
    {
        _collections[16]=null!;_collections[17]=null!;
    }
    private sealed class CollectionOwner
    {
        private Net7AuxDataProperty? _source;
        private SlotOwner[] _slots=[];
        private ImmutableArray<EnbSceneItemSnapshot> _items=[];
        public ImmutableArray<EnbSceneItemSnapshot> Sample(Net7AuxDataProperty? source,byte inventory,
            bool ammo,Net7ItemTemplateCatalog? templates)
        {
            bool changed=false;
            if(!ReferenceEquals(source,_source)||_slots.Length!=(source?.Children.Count??0))
            {
                _source=source;_slots=new SlotOwner[source?.Children.Count??0];
                for(int i=0;i<_slots.Length;i++)_slots[i]=new(source!.Children[i]);
                changed=true;
            }
            int count=0;
            foreach(var slot in _slots)
            {
                changed|=slot.Sample(inventory,ammo,templates);
                if(slot.Item is not null)count++;
            }
            if(changed)
            {
                if(count==0)_items=[];
                else
                {
                    var items=ImmutableArray.CreateBuilder<EnbSceneItemSnapshot>(count);
                    foreach(var slot in _slots)if(slot.Item is {} item)items.Add(item);
                    _items=items.MoveToImmutable();
                }
            }
            return _items;
        }
    }
    private sealed class TextField(Net7AuxDataProperty? source)
    {
        private byte[] _bytes=[];
        private string _text="";
        private bool _wire;
        public string Read()
        {
            if(source?.Value is Net7AuxDataString wire)
            {
                // Wire buffers are mutable; do not trust reference equality.
                if(!_wire||!wire.Bytes.AsSpan().SequenceEqual(_bytes))
                {_bytes=wire.Bytes.ToArray();_text=wire.Text;_wire=true;}
                return _text;
            }
            _wire=false;_bytes=[];return _text=Value(source);
        }
    }
    private readonly record struct Inputs(int Id,int Count,float? Structure,float? Quality,uint? State,
        string Builder,string Information,string ActivatedEffects,string EquipEffects,float? TargetRange,long? ReadyTime,float? EffectRange,
        uint? EffectUsage,uint? EffectTargets,uint? EffectValidity,ulong Price,float? AverageCost,
        byte Inventory,bool Ammo,Net7ItemTemplate? Template);
    private sealed class SlotOwner
    {
        private readonly int _slot;
        private readonly Net7AuxDataProperty? _id,_count,_structure,_quality,_state,_target,_ready,
            _range,_usage,_targets,_validity,_price,_average;
        private readonly TextField _builder,_information,_activatedEffects,_equipEffects;
        private Inputs _inputs;
        private bool _initialized;
        private Net7ItemTemplate? _template,_resolvedTemplate;
        private Net7ItemTemplateCatalog? _catalog;
        private int _lookupId=int.MinValue,_lookupCount;
        private string? _instance;
        private ImmutableArray<EnbItemAttribute> _attributes=[];
        private ImmutableArray<EnbItemSpecial> _effects=[];
        private string _name="Null Item",_description="";
        public EnbSceneItemSnapshot? Item {get;private set;}
        public SlotOwner(Net7AuxDataProperty source)
        {
            _slot=source.ChildIndex;
            _id=Child(source,"ItemTemplateID");_count=Child(source,"StackCount");
            _structure=Child(source,"Structure");_quality=Child(source,"Quality");_state=Child(source,"ItemState");
            _target=Child(source,"TargetRange");_ready=Child(source,"ReadyTime");
            var effects=Child(source,"Effects");_range=Child(effects,"Range");_usage=Child(effects,"Usage");
            _targets=Child(effects,"Targets");_validity=Child(effects,"Validity");
            _price=Child(source,"Price");_average=Child(source,"AveCost");
            _builder=new(Child(source,"BuilderName"));_information=new(Child(source,"InstanceInfo"));
            _activatedEffects=new(Child(source,"InstanceActivatedEffectInfo"));_equipEffects=new(Child(source,"InstanceEquipEffectInfo"));
        }
        private static bool SameBits(float? a,float? b)=>a.HasValue==b.HasValue &&
            (!a.HasValue||BitConverter.SingleToInt32Bits(a.Value)==BitConverter.SingleToInt32Bits(b!.Value));
        public bool Sample(byte inventory,bool ammo,Net7ItemTemplateCatalog? templates)
        {
            int id=Int(_id)??-1;
            if(id<0)
            {
                bool changed=Item is not null;Item=null;_initialized=false;_inputs=default;
                _template=null;_resolvedTemplate=null;_catalog=null;_lookupId=int.MinValue;_instance=null;_attributes=[];_effects=[];_name="Null Item";_description="";
                return changed;
            }
            // The registry retains its first materialized definition. Known
            // slots bind once; unresolved slots retry only on catalog change.
            if(!ReferenceEquals(templates,_catalog)||id!=_lookupId||
                (_resolvedTemplate is null&&_lookupCount!=(templates?.Count??0)))
            {
                _catalog=templates;_lookupId=id;_lookupCount=templates?.Count??0;
                _resolvedTemplate=null;templates?.TryGet((uint)id,out _resolvedTemplate);
            }
            var template=_resolvedTemplate;
            var inputs=new Inputs(id,Int(_count)??0,Float(_structure),Float(_quality),Raw(_state),
                _builder.Read(),_information.Read(),_activatedEffects.Read(),_equipEffects.Read(),Float(_target),(_ready?.Value as Net7AuxDataTime?)?.RelativeValue,
                Float(_range),Raw(_usage),Raw(_targets),Raw(_validity),_price?.Value as ulong? ??0UL,
                Float(_average),inventory,ammo,template);
            // Template identity matters even when record value equality would match.
            if(_initialized&&ReferenceEquals(template,_template)&&inputs==_inputs &&
                SameBits(inputs.Structure,_inputs.Structure)&&SameBits(inputs.Quality,_inputs.Quality)&&
                SameBits(inputs.TargetRange,_inputs.TargetRange)&&SameBits(inputs.EffectRange,_inputs.EffectRange)&&
                SameBits(inputs.AverageCost,_inputs.AverageCost))return false;
            if(!_initialized||!ReferenceEquals(template,_template)||inputs.Information!=_instance||
                inputs.ActivatedEffects!=_inputs.ActivatedEffects||inputs.EquipEffects!=_inputs.EquipEffects)
            {
                (_attributes,_effects)=ProjectItemInformation(template,inputs.Information,inputs.ActivatedEffects,inputs.EquipEffects);
                _name=template?.Name.EffectiveText??"Null Item";
                _description=template?.StringFieldB.EffectiveText??"";
                _template=template;_instance=inputs.Information;
            }
            _inputs=inputs;_initialized=true;
            Item=new(inventory,_slot,id,_name,template?.Category??uint.MaxValue,
                template?.Subcategory??uint.MaxValue,template?.ItemType??uint.MaxValue,template?.TechLevel??0,
                (int?)template?.IconBaseAsset??-1,inputs.Count,inputs.Structure,inputs.Quality,inputs.State,
                _description,inputs.Builder,_attributes,_effects,template?.Flags??0)
                {TargetRange=inputs.TargetRange,ReadyTime=inputs.ReadyTime,EffectRange=inputs.EffectRange,
                 EffectUsage=inputs.EffectUsage,EffectTargets=inputs.EffectTargets,EffectValidity=inputs.EffectValidity,
                 IsAmmoLayer=ammo,CorporationBuilder=template?.StringFieldC.EffectiveText??"",Price=inputs.Price,MaxStack=template?.MaxStack??1,AverageCost=inputs.AverageCost};
            return true;
        }
    }
}
