using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class ScenePacketLayer(int key):List<EnbW3dDrawTask>
    {
        internal int Key {get;}=key;
    }
    // Every invocation rents its own task lists. A nested render cannot reuse
    // the outer view's queued packets, even when it shares physical models.
    private sealed class ScenePacketTasks(EnbWorldRenderer view):IDisposable
    {
        private readonly EnbW3dDrawTaskArena arena=new();
        private EnbW3dDrawTaskArena.Scope scope;
        internal void Enter()=>scope=arena.Enter();
        internal readonly List<EnbW3dDrawTask> Traversal=[];
        internal readonly List<EnbW3dDrawTask> Terrain=[];
        internal readonly EnbW3dFrameSubmissionWorkspace Sort=new();
        private readonly ScenePacketLayer[] layers=[new(-3),new(-2),new(-1),new(0),new(1)];
        internal readonly List<ScenePacketLayer> Active=[];
        internal void Add(int key,EnbW3dDrawTask packet)
        {
            var layer=layers[key+3];
            if(layer.Count==0)
            {
                int position=0;
                while(position<Active.Count && Active[position].Key<key)position++;
                Active.Insert(position,layer);
            }
            layer.Add(packet);
        }
        public void Dispose()
        {
            Traversal.Clear();Terrain.Clear();
            foreach(var layer in Active)layer.Clear();
            Active.Clear();
            scope.Dispose();
            view._availablePacketTasks.Push(this);
        }
    }
    private readonly Stack<ScenePacketTasks> _availablePacketTasks=[];
    private ScenePacketTasks RentPacketTasks()
        {var tasks=_availablePacketTasks.TryPop(out var stored)?stored:new(this);tasks.Enter();return tasks;}
}
