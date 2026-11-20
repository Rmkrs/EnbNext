using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Resource wrapper construction at FILE load (A0AD80/A0CBC0/
/// A0D780/A0FF60/A10A40/A118D0/A12AD0), before duplicate-name publication.</summary>
public sealed class EnbRetailSpecialMaterialTextureOwner : IDisposable
{
    private readonly List<EnbCurrentRetailTextureRegistry.Lease> _leases=[];
    private int _references=1;
    public sealed class Reference : IDisposable
    {
        private EnbRetailSpecialMaterialTextureOwner? _owner;
        internal Reference(EnbRetailSpecialMaterialTextureOwner owner)
        {
            int count;
            do {count=Volatile.Read(ref owner._references);if(count==0)throw new ObjectDisposedException(nameof(EnbRetailSpecialMaterialTextureOwner));}
            while(Interlocked.CompareExchange(ref owner._references,count+1,count)!=count);
            _owner=owner;
        }
        public EnbRetailSpecialMaterialTextureOwner Owner=>_owner??throw new ObjectDisposedException(nameof(Reference));
        public Reference Retain()=>new(Owner);
        public void Dispose(){Interlocked.Exchange(ref _owner,null)?.Release();GC.SuppressFinalize(this);}
        ~Reference(){Interlocked.Exchange(ref _owner,null)?.Release();}
    }
    public Reference Retain()=>new(this);
    public EnbW3dRenderTextureBinding? Base { get; }
    public EnbW3dRenderTextureBinding? Normal { get; }
    public EnbRetailCubeTexture? Cube { get; }
    public EnbRetailSpecialMaterialTextureOwner(EnbCurrentRetailSpecialMaterialResource resource,
        Func<string,EnbCurrentRetailTextureRegistry.Lease> acquire,string workingDirectory)
    {
        try
        {
            if(resource.Selector==5)
            {if(EnbRetailCubeTexture.TryLoad(workingDirectory,resource.Texture,out var cube))Cube=cube;}
            else
            {
                Base=Bind(resource.Texture,0);
                if(resource.Selector is 3 or 4)Normal=Bind(resource.SecondTexture,1);
            }
        }
        catch {Dispose();throw;}
        EnbW3dRenderTextureBinding? Bind(string name,uint ordinal)
        {
            // 91DE37..4B returns NULL for a null/empty name.
            if(name.Length==0)return null;
            var lease=acquire(name);_leases.Add(lease);
            return new(ordinal,name,null){ResolveLogicalNameAtRuntime=true,CurrentRetailTextureState=lease.State};
        }
    }
    private int _disposed;
    public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)==0)Release();}
    private void Release(){if(Interlocked.Decrement(ref _references)==0){foreach(var lease in _leases)lease.Dispose();_leases.Clear();}}
}
