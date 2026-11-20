namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private sealed class LegoDrawPlan
    {
        public readonly List<LiveShipLegoSpec> Specs;
        public readonly LiveShipLegoSpec[] Ordered;
        public readonly int[] ParentSlots;
        public readonly string? DuplicatePath;
        private readonly List<Frame> _frames=[];
        public LegoDrawPlan(IReadOnlyList<LiveShipLegoSpec> specs,bool physical)
        {
            Specs=new(specs);
            Ordered=physical?specs.ToArray():specs.OrderBy(item=>item.Path.Length).ThenBy(item=>item.Source,StringComparer.Ordinal).ThenBy(item=>item.Ordinal).ToArray();
            DuplicatePath=specs.GroupBy(spec=>spec.PhysicalDrawPath??spec.CanonicalPath,StringComparer.Ordinal).FirstOrDefault(group=>group.Count()>1)?.Key;
            var slots=new Dictionary<string,int>(StringComparer.Ordinal){{string.Empty,0}};
            for(int i=0;i<Ordered.Length;i++)slots.TryAdd(Ordered[i].PhysicalDrawPath??Ordered[i].CanonicalPath,i+1);
            ParentSlots=Ordered.Select(spec=>slots.GetValueOrDefault(spec.PhysicalParentPath??spec.LogicalParentPath,-1)).ToArray();
        }
        public Frame Rent()
        {
            foreach(var frame in _frames)if(!frame.Active){frame.Active=true;return frame;}
            var created=new Frame(Ordered.Length+1){Active=true};_frames.Add(created);return created;
        }
        public sealed class Frame(int count):IDisposable
        {
            public bool Active;
            public readonly ShipAttachmentOwner?[] Parents=new ShipAttachmentOwner?[count];
            public void Dispose(){Array.Clear(Parents);Active=false;}
        }
    }
}
