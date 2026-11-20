using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Retained decal descriptors for one installed asset namespace. Empty, missing
/// and non-mesh prototypes retain the manager's default (0075BE00). Unsupported
/// decoding is reported separately and is never mistaken for native failure.
/// </summary>
public sealed class EnbCurrentRetailSwatchCatalog : IDisposable
{
    private readonly Dictionary<int, (EnbCurrentRetailSwatchDescriptor? Descriptor, string? Diagnostic)> _entries = [];
    private readonly Dictionary<int,EnbCurrentRetailTextureRegistry.Lease> _textures=[];
    public bool TryGetTexture(int decalId,out EnbCurrentRetailTextureObjectState texture)
    {
        if(_textures.TryGetValue(decalId,out var lease)) {texture=lease.State;return true;}
        texture=null!;return false;
    }
    public void Dispose() {foreach(var lease in _textures.Values)lease.Dispose();_textures.Clear();}
    public bool TryGet(int decalId, out EnbCurrentRetailSwatchDescriptor descriptor, out string? diagnostic)
    {
        if (_entries.TryGetValue(decalId, out var entry))
        {
            descriptor = entry.Descriptor!;
            diagnostic = entry.Diagnostic;
            return descriptor is not null;
        }
        descriptor = default!;
        diagnostic = $"Swatch id {decalId} is absent from the installed catalog.";
        return false;
    }

    public static EnbCurrentRetailSwatchCatalog Load(EnbDecalDefinitionCatalog definitions,
        EnbMixResourceCatalog resources, uint constructionClock, EnbW3dRenderResourceCache? prototypes = null,
        EnbRetailFileResolver? files = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(resources);
        var result = new EnbCurrentRetailSwatchCatalog();
        //0075BE00 loads normal textures, then secret textures, then swatches.
        //0082EF70 requests the shared name registry's selector0/format0.
        //The manager unconditionally clamps both address words afterwards.
        //A supplied prototype namespace owns publication and creation settings.
        if(prototypes is not null)
            foreach(var definition in definitions.NormalTextures.Concat(definitions.SecretTextures))
            {
                var lease=prototypes.AcquireNamedTexture(definition.Resource);
                lease.State.ApplyRslEffectPassAddressing(); //Identical +64/+68 writes.
                result._textures.Add(definition.DecalId,lease);
            }
        prototypes ??= new EnbW3dRenderResourceCache();
        var fallbackPass = EnbCurrentRetailShipDecalRuntime.CreateSwatchDefaultDescriptor();
        // 0075BE00 owns the default descriptor; it is not an asset prototype.
        if (!EnbCurrentRetailSwatchDescriptor.TryCreate([fallbackPass], constructionClock,
                new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance),
                out var nativeDefault, out var defaultDiagnostic))
            throw new InvalidOperationException(defaultDiagnostic);

        foreach (var definition in definitions.Swatches)
        {
            string? token = definition.SwatchPrototypeName;
            if (string.IsNullOrEmpty(token) || EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(token))
            {
                result._entries.Add(definition.DecalId, (nativeDefault, null));
                continue;
            }

            EnbW3dRenderResource? source = null;
            EnbW3dRenderMesh? mesh = null;
            string? diagnostic = null;
            // 0091D4B0: look up the raw token, then lazy-load its container
            // prefix. 0075BE00 retries with literal token + ".w3d".
            int dot = token.IndexOf('.');
            string lazyName = (dot < 0 ? token : token[..dot]) + ".w3d";
            //0075C52B explicitly loads FILE, then0075C53E performs a fresh
            //ordinary lookup. An unsuccessful lookup may lazy-load the same
            //file again; those constructors must not be deduplicated.
            foreach (var attempt in new[] { lazyName, definition.Resource, lazyName }.Select((file,index)=>(File:file,Index:index)))
            {
                bool registered=prototypes.TryFindPrototypeSource(token,out var selected);
                if (attempt.Index==1 || !registered)
                {
                    string file=attempt.File;
                    bool Read(string candidate,out byte[] content,out EnbMixResourceLocation location,out string? loose)
                    {
                        loose=null;
                        if(files is null)return resources.TryReadResource(candidate,out content,out location);
                        bool found=files.TryRead(candidate,out content,out var package,out loose);
                        location=found?package??new(candidate,loose!,0,content.Length):null!;
                        return found;
                    }
                    if (!Read(file, out var bytes, out var location,out var loosePath))
                    {
                        //Only ordinary manager acquisition retries the parent.
                        if (attempt.Index==1 || !Read("..\\" + file, out bytes, out location,out loosePath))continue;
                        file="..\\"+file;
                    }
                    if(bytes.Length==0)continue; //Successful empty load publishes nothing.
                    //FILE publishes definitions before NAME creates an object.
                    if (!EnbRetailW3dFileResolver.TryDecodeFile(file,token,bytes,location,loosePath,resources,out var asset,out diagnostic))break;
                    if (!prototypes.TryLoadFile(asset, out source, out diagnostic, constructionClock))
                        break;
                    if (!prototypes.TryFindPrototypeSource(token, out selected)) continue;
                }
                //0075C50E/0075C53E acquires one registered object;0075C553
                //accepts its class0 descriptor. Never select a different mesh
                //from the same file, including a later duplicate name.
                if(selected.AggregatePrototype is not null)
                {
                    if(files is null)
                    {diagnostic="Aggregate swatch creation requires the retail file provider.";break;}
                    bool created=TryCreateAggregateBase(selected,files,resources,prototypes,constructionClock,
                        out selected,out diagnostic,out bool missing);
                    if(!created)
                    {
                        if(!missing)break;
                        diagnostic=null;
                        if(attempt.Index!=0)break;
                        continue;
                    }
                }
                if (selected.RegisteredPrototype is not {Kind: EnbW3dPrototypeKind.Mesh} entry) break;
                if (!prototypes.TryGetRegisteredPrototype(selected, out source))
                    throw new InvalidOperationException("The selected swatch prototype was removed during acquisition.");
                mesh = source.Meshes[entry.Ordinal];
                break;
            }
            if(mesh is null && diagnostic is null && !prototypes.TryFindPrototypeSource(token,out _) &&
                prototypes.CapturePrototypeSources(out _).Values.Any(value=>value.PrototypeFile?.HasUnrepresentedRenderPrototypes==true ||
                    value.AcquisitionPublications.Any(publication=>publication.PrototypeFile?.HasUnrepresentedRenderPrototypes==true)))
                diagnostic="Swatch name absence is unproven because retained FILE publications contain undecoded render prototypes.";
            if (diagnostic is not null)
                result._entries.Add(definition.DecalId, (null, diagnostic));
            else if (mesh is null)
                result._entries.Add(definition.DecalId, (nativeDefault, null));
            else if (prototypes.TryRetainDescriptor(mesh.MaterialPasses, constructionClock, out var descriptor, out diagnostic))
                result._entries.Add(definition.DecalId, (descriptor, null));
            else
                result._entries.Add(definition.DecalId, (null, diagnostic));
        }
        return result;
    }

    internal static bool TryCreateAggregateBase(EnbResolvedW3dResource source,EnbRetailFileResolver files,
        EnbMixResourceCatalog resources,EnbW3dRenderResourceCache prototypes,uint clock,
        out EnbResolvedW3dResource selected,out string? diagnostic,out bool nativeMissing)
    {
        selected=default!;
        bool created=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(files,resources,source,
            prototypes.CapturePrototypeSources(out _),out var resolved,out diagnostic,out var publications,out nativeMissing);
        foreach(var publication in publications)
            if(!prototypes.TryLoadFile(publication,out _,out var publicationError,clock))
                throw new InvalidDataException(publicationError);
        if(!created || !CreateAggregateChildren(resolved,files,resources,prototypes,clock,out diagnostic))return false;
        var aggregate=resolved.CurrentRetailAggregate!;
        string baseName=aggregate.BaseRequestedObjectName;
        if(EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(baseName))
        {
            selected=resolved with {RequestedObjectName=baseName,CurrentRetailAggregate=null,
                RegisteredPrototype=aggregate.BaseRegisteredPrototype};
            return true;
        }
        if(!prototypes.TryFindPrototypeSource(baseName,out selected))
            throw new InvalidOperationException("The created swatch base was not published.");
        return true;
    }
    private static bool CreateAggregateChildren(EnbResolvedW3dResource source,EnbRetailFileResolver files,
        EnbMixResourceCatalog resources,EnbW3dRenderResourceCache prototypes,uint clock,out string? diagnostic)
    {
        diagnostic=null;
        var active=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack=new Stack<(EnbResolvedW3dResource Source,int Next)>();
        stack.Push((source,0));active.Add(source.RequestedObjectName);
        while(stack.TryPop(out var frame))
        {
            var children=frame.Source.CurrentRetailAggregate!.RenderDefinition.Attachments;
            if(frame.Next==children.Count){active.Remove(frame.Source.RequestedObjectName);continue;}
            stack.Push((frame.Source,frame.Next+1));
            string name=children[frame.Next].SubobjectName;
            if(active.Contains(name)){diagnostic=$"Recursive aggregate swatch child '{name}' is outside supported creation.";return false;}
            bool created=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(files,resources,name,
                out var child,out diagnostic,out var publications,out bool missing,prototypes.CapturePrototypeSources(out _));
            foreach(var publication in publications)
                if(!prototypes.TryLoadFile(publication,out _,out var publicationError,clock))
                    throw new InvalidDataException(publicationError);
            if(!created)
            {
                if(!missing)return false;
                diagnostic=null;continue;
            }
            if(child.CurrentRetailAggregate is not null)
            {active.Add(child.RequestedObjectName);stack.Push((child,0));}
        }
        return true;
    }
}
