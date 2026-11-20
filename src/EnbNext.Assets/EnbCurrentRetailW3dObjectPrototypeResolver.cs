using System.Buffers.Binary;

namespace EnbNext.Assets;

/// <summary>
/// Asset-side mirror of current retail WW3DAssetManager primary lazy filename
/// lookup. It does not collapse RSL OBJECT and FILE:
/// callers opt into this runtime lookup only when reproducing Create_Render_Object.
/// R6Z11 additionally recognizes the current-retail 0x0600 Aggregate prototype
/// family and resolves its referenced base RenderObj while retaining authored
/// attachment metadata separately.
/// </summary>
public static class EnbCurrentRetailW3dObjectPrototypeResolver
{
    private static bool CanProveMissing(IReadOnlyDictionary<string,EnbResolvedW3dResource>? registered,
        ref string? diagnostic,EnbResolvedW3dResource? source=null)
    {
        static bool Partial(EnbResolvedW3dResource value)=>value.PrototypeFile?.HasUnrepresentedRenderPrototypes==true ||
            value.AcquisitionPublications.Any(publication=>publication.PrototypeFile?.HasUnrepresentedRenderPrototypes==true);
        if((source is not null && Partial(source)) || registered?.Values.Any(Partial)==true)
        {
            diagnostic=$"Native name absence is unproven because retained FILE publications include render prototypes outside the current model boundary. {diagnostic}";
            return false;
        }
        return true;
    }

    private static bool TryCreateBuiltin(string name,out EnbResolvedW3dResource resource)
    {
        resource=default!;
        //0091D4E2..4F9 selects00BFAEA8 before the ordinary hash/file path.
        if(!string.Equals(name,"NULL",StringComparison.OrdinalIgnoreCase))return false;
        resource=new("<builtin:NULL>","NULL",new("NULL","<retail-builtin>",0,0),EnbW3dModel.CreateBuiltinNullObject(),[])
            {RegisteredPrototype=new(EnbW3dPrototypeKind.NullObject,0,0)};
        return true;
    }

    /// <summary>009A3A50 acquires each attachment through009A3AB0 too:
    /// existing prototype, ordinary lookup, then full-name working-directory
    /// FILE and another lookup. This extra retry is aggregate-specific.</summary>
    public static bool TryResolveAggregateChild(EnbRetailFileResolver files,
        EnbMixResourceCatalog? resources,string name,out EnbResolvedW3dResource resource,out string? diagnostic,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null)
        => TryResolveAggregateChild(files,resources,name,out resource,out diagnostic,out _,registeredPrototypes);

    public static bool TryResolveAggregateChild(EnbRetailFileResolver files,
        EnbMixResourceCatalog? resources,string name,out EnbResolvedW3dResource resource,out string? diagnostic,
        out IReadOnlyList<EnbResolvedW3dResource> acquisitionPublications,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null)
        => TryResolveAggregateChild(files,resources,name,out resource,out diagnostic,out acquisitionPublications,out _,registeredPrototypes);

    public static bool TryResolveAggregateChild(EnbRetailFileResolver files,
        EnbMixResourceCatalog? resources,string name,out EnbResolvedW3dResource resource,out string? diagnostic,
        out IReadOnlyList<EnbResolvedW3dResource> acquisitionPublications,out bool nativeNameMissing,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null)
    {
        bool missing=false;
        bool success=TryResolveAggregateChildCore(files,resources,name,out resource,out diagnostic,out acquisitionPublications,
            registeredPrototypes,()=>missing=true);
        nativeNameMissing=!success && missing && CanProveMissing(registeredPrototypes,ref diagnostic);return success;
    }

    private static bool TryResolveAggregateChildCore(EnbRetailFileResolver files,
        EnbMixResourceCatalog? resources,string name,out EnbResolvedW3dResource resource,out string? diagnostic,
        out IReadOnlyList<EnbResolvedW3dResource> acquisitionPublications,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes,Action onNativeMissing)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(name);
        var publications=new List<EnbResolvedW3dResource>();acquisitionPublications=publications;
        if(TryCreateBuiltin(name,out resource)){diagnostic=null;return true;}
        if(registeredPrototypes?.TryGetValue(name,out var registered)==true)
        {
            if(registered.AggregatePrototype is not {} aggregate)
            {resource=registered with {RequestedObjectName=name};diagnostic=null;return true;}
            return TryResolveLoadedResource(resources,registered.LogicalFileName,name,aggregate.AggregateBytes.ToArray(),registered.ResourceLocation,
                out resource,out diagnostic,files,registered.LooseFilePath,registeredPrototypes,registered,publications,retryRootAfterFailure:true,onNativeMissing:onNativeMissing);
        }
        EnbResolvedW3dResource? Select(EnbResolvedW3dResource source)
        {
            if(source.CurrentRetailAggregate is not null)return source;
            foreach(var entry in source.Model.PrototypeEntries)
                if(string.Equals(PrototypeName(source.Model,entry),name,StringComparison.OrdinalIgnoreCase))
                    return source with {RegisteredPrototype=entry};
            return null;
        }
        bool acquired;
        if(TryReadNamedResource(resources,name,files,out var file,out var bytes,out var location,out var path,out diagnostic))
            acquired=TryResolveLoadedResource(resources,file,name,bytes,location,out resource,out diagnostic,files,path,
                registeredPrototypes,acquisitionPublications:publications,retryRootAfterFailure:true,onNativeMissing:onNativeMissing);
        else if(files.TryReadAggregateWorkingDirectoryFile(name,out bytes,out var package,out path))
            acquired=TryResolveLoadedResource(resources,path!,name,bytes,package??new(path!,path!,0,bytes.Length),
                out resource,out diagnostic,files,path,registeredPrototypes,acquisitionPublications:publications,onNativeMissing:onNativeMissing);
        else {resource=default!;acquired=false;onNativeMissing();}
        if(acquired && Select(resource) is {} selected){resource=selected;return true;}
        if(acquired && !publications.Any(p=>p.PrototypeFile?.HasUnrepresentedRenderPrototypes==true))onNativeMissing();
        resource=default!;
        diagnostic??=$"Aggregate attachment prototype '{name}' is not available after ordinary and full-name file acquisition.";
        return false;
    }

    /// <summary>Create an already-published aggregate from its retained
    /// definition.009A4540 acquires the current base at creation time; it does
    /// not reopen the aggregate's original file or freeze its earlier base.</summary>
    public static bool TryResolveRegisteredSource(EnbRetailFileResolver files,EnbMixResourceCatalog? resources,
        EnbResolvedW3dResource source,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes,
        out EnbResolvedW3dResource resource,out string? diagnostic)
        => TryResolveRegisteredSource(files,resources,source,registeredPrototypes,out resource,out diagnostic,out _);

    /// <summary>FILE publications survive a later creation failure. Return
    /// all decoded loads so the owning manager can retain them on either path.</summary>
    public static bool TryResolveRegisteredSource(EnbRetailFileResolver files,EnbMixResourceCatalog? resources,
        EnbResolvedW3dResource source,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes,
        out EnbResolvedW3dResource resource,out string? diagnostic,out IReadOnlyList<EnbResolvedW3dResource> acquisitionPublications)
        => TryResolveRegisteredSource(files,resources,source,registeredPrototypes,out resource,out diagnostic,out acquisitionPublications,out _);

    /// <summary>nativeNameMissing is true only for a proven exhausted name
    /// acquisition, never an unsupported decoder or recursive graph.</summary>
    public static bool TryResolveRegisteredSource(EnbRetailFileResolver files,EnbMixResourceCatalog? resources,
        EnbResolvedW3dResource source,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes,
        out EnbResolvedW3dResource resource,out string? diagnostic,out IReadOnlyList<EnbResolvedW3dResource> acquisitionPublications,
        out bool nativeNameMissing)
    {
        nativeNameMissing=false;
        var publications=new List<EnbResolvedW3dResource>();
        acquisitionPublications=publications;
        if(source.AggregatePrototype is not {} aggregate)
        {resource=source;diagnostic=null;return true;}
        bool missing=false;
        bool success=TryResolveLoadedResource(resources,source.LogicalFileName,source.RequestedObjectName,
            aggregate.AggregateBytes.ToArray(),source.ResourceLocation,out resource,out diagnostic,files,source.LooseFilePath,
            registeredPrototypes,source,publications,onNativeMissing:()=>missing=true);
        nativeNameMissing=!success && missing && CanProveMissing(registeredPrototypes,ref diagnostic,source);return success;
    }

    public static bool TryResolveSimpleName(EnbRetailFileResolver files,
        EnbMixResourceCatalog? textures, string prototypeName,
        out EnbResolvedW3dResource resource, out string? diagnostic,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes = null)
        => TryResolveSimpleName(files,textures,prototypeName,out resource,out diagnostic,out _,registeredPrototypes);

    public static bool TryResolveSimpleName(EnbRetailFileResolver files,
        EnbMixResourceCatalog? textures,string prototypeName,
        out EnbResolvedW3dResource resource,out string? diagnostic,out IReadOnlyList<EnbResolvedW3dResource> acquisitionPublications,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null)
        => TryResolveSimpleName(files,textures,prototypeName,out resource,out diagnostic,out acquisitionPublications,out _,registeredPrototypes);

    public static bool TryResolveSimpleName(EnbRetailFileResolver files,
        EnbMixResourceCatalog? textures,string prototypeName,
        out EnbResolvedW3dResource resource,out string? diagnostic,out IReadOnlyList<EnbResolvedW3dResource> acquisitionPublications,
        out bool nativeNameMissing,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(prototypeName);
        var publications=new List<EnbResolvedW3dResource>();
        acquisitionPublications=publications;
        bool missing=false;
        bool success=TryResolveSimpleNameCore(textures, prototypeName,
            out resource, out diagnostic, files, registeredPrototypes,publications,()=>missing=true);
        nativeNameMissing=!success && missing && CanProveMissing(registeredPrototypes,ref diagnostic);return success;
    }

    /// <summary>The existing API name also covers dotted OBJECT names:
    ///0091D4B0 loads the first-dot prefix and retries the full prototype name.</summary>
    public static bool TryResolveSimpleName(
        EnbMixResourceCatalog? resources,
        string prototypeName,
        out EnbResolvedW3dResource resource,
        out string? diagnostic)
    {
        resource = default!;
        diagnostic = null;
        //0091D549..0091D590 also formats an empty name as '.w3d'.
        ArgumentNullException.ThrowIfNull(prototypeName);

        string requestedObjectName = prototypeName;
        if(TryCreateBuiltin(requestedObjectName,out resource))return true;
        if (resources is null)
        {
            diagnostic = "installed MIX resource catalog is unavailable.";
            return false;
        }

        return TryResolveSimpleNameCore(
            resources,
            requestedObjectName,
            out resource,
            out diagnostic);
    }

    /// <summary>
    /// Resolves an already-authored FILE/NAME resource using the same W3D family
    /// boundary as the primary lazy path. Unlike TryResolveSimpleName this
    /// does not derive the filename because FILE is already
    /// authored by basset.ini/RSL.
    /// </summary>
    public static bool TryResolveAuthoredResource(
        EnbMixResourceCatalog? resources,
        string logicalFileName,
        string requestedObjectName,
        byte[] bytes,
        EnbMixResourceLocation location,
        out EnbResolvedW3dResource resource,
        out string? diagnostic,
        EnbRetailFileResolver? files = null,
        string? looseFilePath = null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalFileName);
        ArgumentNullException.ThrowIfNull(requestedObjectName);
        ArgumentNullException.ThrowIfNull(bytes);

        resource = default!;
        diagnostic = null;
        if (resources is null && files is null)
        {
            diagnostic = "installed MIX resource catalog is unavailable.";
            return false;
        }

        return TryResolveLoadedResource(
            resources,
            logicalFileName,
            requestedObjectName,
            bytes,
            location,
            out resource,
            out diagnostic, files, looseFilePath, registeredPrototypes);
    }

    private static bool TryResolveSimpleNameCore(
        EnbMixResourceCatalog? resources,
        string requestedObjectName,
        out EnbResolvedW3dResource resource,
        out string? diagnostic,
        EnbRetailFileResolver? files = null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes = null,
        List<EnbResolvedW3dResource>? acquisitionPublications=null,Action? onNativeMissing=null)
    {
        resource = default!;
        diagnostic = null;
        if(TryCreateBuiltin(requestedObjectName,out resource))return true;
        EnbResolvedW3dResource? registered=null;
        registeredPrototypes?.TryGetValue(requestedObjectName,out registered);
        registered??=(registeredPrototypes as EnbCurrentRetailW3dPrototypeNamespace)?.PublicationContext?.FindRegisteredSource(requestedObjectName);
        if(registered is not null)
        {
            if(registered.AggregatePrototype is not {} aggregate)
            {resource=registered with {RequestedObjectName=requestedObjectName};return true;}
            return TryResolveLoadedResource(resources,registered.LogicalFileName,requestedObjectName,aggregate.AggregateBytes.ToArray(),
                registered.ResourceLocation,out resource,out diagnostic,files,registered.LooseFilePath,registeredPrototypes,
                registered,acquisitionPublications,onNativeMissing:onNativeMissing);
        }
        if(!TryReadNamedResource(resources,requestedObjectName,files,out var logicalFileName,out var bytes,
            out var location,out var loosePath,out diagnostic)){onNativeMissing?.Invoke();return false;}
        return TryResolveLoadedResource(resources,logicalFileName,requestedObjectName,bytes,location,
            out resource,out diagnostic,files,loosePath,registeredPrototypes,acquisitionPublications:acquisitionPublications,onNativeMissing:onNativeMissing);
    }

    private static bool TryReadNamedResource(EnbMixResourceCatalog? resources,string requestedObjectName,EnbRetailFileResolver? files,
        out string logicalFileName,out byte[] bytes,out EnbMixResourceLocation location,out string? loosePath,out string? diagnostic)
    {
        diagnostic=null;

        //0091D557 finds the first dot.0091D56D copies only the prefix,
        //then0091D57D appends '.w3d'; the registry retry retains the full name.
        int separator = requestedObjectName.IndexOf('.');
        logicalFileName = (separator < 0 ? requestedObjectName : requestedObjectName[..separator]) + ".w3d";
        bytes = [];
        location = null!;
        loosePath = null;
        bool found;
        if (files is not null)
        {
            found = files.TryRead(logicalFileName, out bytes, out var providerLocation, out loosePath);
            //0091D5A6 branches around the secondary load on primary success,
            //even if that file publishes no matching named prototype.
            if (!found)
            {
                logicalFileName = "..\\" + logicalFileName;
                found = files.TryRead(logicalFileName, out bytes, out providerLocation, out loosePath);
            }
            if (found)
                location = providerLocation ?? new(logicalFileName, loosePath!, 0, bytes.Length);
        }
        else found = resources is not null && resources.TryReadResource(logicalFileName, out bytes, out location);
        if (!found)
        {
            diagnostic = $"lazy prototype resource '{logicalFileName}' is not available in the installed MIX archives.";
            return false;
        }

        return true;
    }

    private static bool TryResolveLoadedResource(
        EnbMixResourceCatalog? resources,
        string logicalFileName,
        string requestedObjectName,
        byte[] bytes,
        EnbMixResourceLocation location,
        out EnbResolvedW3dResource resource,
        out string? diagnostic,
        EnbRetailFileResolver? files = null,string? loosePath=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes = null,
        EnbResolvedW3dResource? registeredInput = null,List<EnbResolvedW3dResource>? acquisitionPublications=null,
        bool retryRootAfterFailure=false,Action? onNativeMissing=null)
    {
        resource = default!;
        diagnostic = null;

        var wrappers=new List<(string File,string Name,EnbMixResourceLocation Location,string? Loose,EnbCurrentRetailW3dAggregateDefinition Definition,long Registration)>();
        var publications=acquisitionPublications??new List<EnbResolvedW3dResource>();
        var aggregateStack=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var publishedFiles=new List<(string File,byte[] Bytes,EnbMixResourceLocation Location,string? Loose,EnbW3dModel? Model)>();
        var publicationContext=(registeredPrototypes as EnbCurrentRetailW3dPrototypeNamespace)?.PublicationContext;
        bool HasUnrepresentedPublications()=>publications.Any(file=>file.PrototypeFile?.HasUnrepresentedRenderPrototypes==true);
        EnbResolvedW3dResource BindPublication(EnbCurrentRetailW3dPrototypeFile publication,string file,string name,
            EnbMixResourceLocation sourceLocation,string? path)
        {
            var decoded=new EnbResolvedW3dResource(file,name,sourceLocation,publication.Model,
                EnbW3dResourceDependencies.ResolveTextureDependencies(publication.Model,resources))
                {LooseFilePath=path,PrototypeFile=publication};
            return publicationContext?.Publish(decoded)??decoded;
        }
        void ReportMissing()
        {
            if(!HasUnrepresentedPublications())onNativeMissing?.Invoke();
        }
        var baseFileRetries=new List<bool>();
        bool TryAggregateWorkingDirectory()
        {
            if (wrappers.Count == 0 || baseFileRetries[^1] || files is null) return false;
            baseFileRetries[^1] = true;
            return TryFullNameFileAndAcquire();
        }
        bool TryFullNameFileAndAcquire()
        {
            if (files is null || !files.TryReadAggregateWorkingDirectoryFile(requestedObjectName,
                out var fallbackBytes, out var fallbackLocation, out var fallbackPath)) return false;
            bytes = fallbackBytes;
            loosePath = fallbackPath;
            logicalFileName = fallbackPath!;
            location = fallbackLocation ?? new(logicalFileName, fallbackPath!, 0, bytes.Length);
            //The explicit FILE is published before ordinary acquisition runs
            //again. An existing aggregate definition still wins that lookup.
            bool decoded=EnbW3dModel.TryDecode(bytes,out var fallbackModel);
            bool decodedPublication=EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,decoded?fallbackModel:null,out var publication,out _);
            if(decodedPublication)
            {
                var bound=BindPublication(publication,logicalFileName,requestedObjectName,location,loosePath);
                if(bytes.Length!=0)publications.Add(bound);
                fallbackModel=bound.Model;
                decoded|=bound.Model.PrototypeEntries.Count!=0;
            }
            publishedFiles.Add((logicalFileName,bytes,location,loosePath,decoded?fallbackModel:null));
            registeredInput=null;
            if(registeredPrototypes?.TryGetValue(requestedObjectName,out var retained)==true && retained.AggregatePrototype is {} retainedAggregate)
            {
                registeredInput=retained;
                logicalFileName=retained.LogicalFileName;location=retained.ResourceLocation;loosePath=retained.LooseFilePath;
                bytes=retainedAggregate.AggregateBytes.ToArray();
            }
            else if(decodedPublication && !publishedFiles.Any(file=>
                (file.Model is {} known && HasPrototype(known,requestedObjectName)) ||
                TryFindAggregate(file.Bytes,requestedObjectName,out _,out _)))
            {
                //009A3AE0 calls ordinary Create_Render_Object again. If the
                //explicit FILE published no requested name,0091D4B0 repeats
                //the first-dot lazy load; duplicate constructors still run.
                if(TryReadNamedResource(resources,requestedObjectName,files,out var lazyFile,out var lazyBytes,
                    out var lazyLocation,out var lazyPath,out _))
                {logicalFileName=lazyFile;bytes=lazyBytes;location=lazyLocation;loosePath=lazyPath;}
            }
            return true;
        }
        bool TryRetryFailedBase()
        {
            //009A4540 returns a failed nested creation to its caller's
            //009A3AB0. That parent still has its own full-name FILE retry.
            //Keep retry state per live frame, without a CLR recursion limit.
            string failedName=requestedObjectName;
            while(wrappers.Count!=0)
            {
                if(TryAggregateWorkingDirectory())return true;
                var failed=wrappers[^1];wrappers.RemoveAt(wrappers.Count-1);
                baseFileRetries.RemoveAt(baseFileRetries.Count-1);
                aggregateStack.Remove($"{failed.Location.ArchiveName}|{failed.Location.Name}|{failed.Definition.Name}");
                requestedObjectName=failed.Name;
            }
            if(retryRootAfterFailure)
            {
                retryRootAfterFailure=false;
                if(TryFullNameFileAndAcquire())return true;
            }
            requestedObjectName=failedName;
            return false;
        }
        //009A4540 ->009A3AB0 asks the ordinary manager for each base. There
        //is no eight-level selector. Walk the chain without using the CLR call
        //stack; retain cycle containment as a local malformed-asset failure.
        while(true)
        {
            bool decodedModel=EnbW3dModel.TryDecode(bytes, out EnbW3dModel model);
            if(!publishedFiles.Any(file=>ReferenceEquals(file.Bytes,bytes)))
            {
                if(registeredInput is null && EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,decodedModel?model:null,out var publication,out _))
                {
                    var bound=BindPublication(publication,logicalFileName,requestedObjectName,location,loosePath);
                    if(bytes.Length!=0)publications.Add(bound);
                    model=bound.Model;
                    //A partial FILE can supply ordinary roots even when the
                    //strict model decode failed. Aggregate-only files remain
                    //publication-only until their base is acquired.
                    decodedModel|=bound.Model.PrototypeEntries.Count!=0;
                }
                publishedFiles.Add((logicalFileName,bytes,location,loosePath,decodedModel?model:null));
            }
            //A nested load does not erase earlier publications. Resolve the
            //first matching source from this acquisition's loaded files before
            //using the newly loaded file, including duplicate base names.
            if(registeredInput is null)foreach(var file in publishedFiles)
                if((file.Model is {} known && HasPrototype(known,requestedObjectName)) ||
                    TryFindAggregate(file.Bytes,requestedObjectName,out _,out _))
                {
                    logicalFileName=file.File;bytes=file.Bytes;location=file.Location;loosePath=file.Loose;
                    decodedModel=file.Model is not null;model=file.Model!;
                    break;
                }
            bool matchedAggregate=TryFindAggregate(bytes,requestedObjectName,out var aggregate,out int aggregateOffset);
            //0091D270 registers every top-level prototype before0091D4B0
            //creates the requested object.0091D370 retains the first name.
            bool ordinaryFirst=decodedModel && model.PrototypeEntries.Any(entry=>entry.ChunkOffset<aggregateOffset &&
                string.Equals(PrototypeName(model,entry),requestedObjectName,StringComparison.OrdinalIgnoreCase));
            if (decodedModel && (!matchedAggregate || ordinaryFirst))
            {
                if(!HasPrototype(model,requestedObjectName) && HasUnrepresentedPublications())
                {
                    if(TryRetryFailedBase())continue;
                    diagnostic=$"Prototype '{requestedObjectName}' is not represented by the current model decoder; a loaded file contains additional render prototype families.";
                    return false;
                }
                //009A3AC7 tests Create_Render_Object's result, not Load_File's.
                //A readable file with no matching prototype still reaches the
                //aggregate's full-name working-directory retry (009A3AD0).
                if ((wrappers.Count != 0 || retryRootAfterFailure) && !HasPrototype(model, requestedObjectName))
                {
                    if(TryRetryFailedBase())continue;
                    //009A4540 returns the failed base creation result. A
                    //decoded FILE is not evidence that this NAME exists.
                    diagnostic=$"0x0600 aggregate base prototype '{requestedObjectName}' is absent after ordinary and full-name file acquisition.";
                    ReportMissing();
                    return false;
                }
                resource = new(logicalFileName,requestedObjectName,location,model,
                    EnbW3dResourceDependencies.ResolveTextureDependencies(model, resources)){LooseFilePath=loosePath};
                if(publicationContext?.FindRegisteredSource(requestedObjectName) is {AggregatePrototype:null} publishedSource)
                    resource=publishedSource with {RequestedObjectName=requestedObjectName};
                break;
            }
            if (!matchedAggregate && !EnbCurrentRetailW3dAggregateDecoder.TryDecode(
                bytes,
                out aggregate,
                out string? aggregateDiagnostic))
            {
                if (TryRetryFailedBase()) continue;
                if(bytes.Length==0)ReportMissing();
                diagnostic =
                    $"lazy prototype resource '{location.Name}' in '{location.ArchiveName}' is not decoded by the current EnbNext W3D model boundary" +
                    (string.IsNullOrWhiteSpace(aggregateDiagnostic) ? "." : $"; 0x0600 aggregate probe: {aggregateDiagnostic}");
                return false;
            }

            if (!string.Equals(aggregate.Name, requestedObjectName, StringComparison.OrdinalIgnoreCase))
            {
                if (TryRetryFailedBase()) continue;
                diagnostic =
                    $"0x0600 aggregate '{location.Name}' registers prototype '{aggregate.Name}', not requested object '{requestedObjectName}'.";
                ReportMissing();
                return false;
            }
            string stackKey = $"{location.ArchiveName}|{location.Name}|{aggregate.Name}";
            if (!aggregateStack.Add(stackKey))
            {
                diagnostic = $"0x0600 aggregate '{aggregate.Name}' forms a recursive prototype cycle.";
                return false;
            }

            wrappers.Add((logicalFileName,requestedObjectName,location,loosePath,aggregate,
                registeredInput?.PrototypeRegistration??publicationContext?.FindRegisteredSource(requestedObjectName)?.PrototypeRegistration??0));
            baseFileRetries.Add(false);
            registeredInput=null;
            requestedObjectName=aggregate.BaseModelName;
            if(TryCreateBuiltin(requestedObjectName,out resource))break;
            //009A3AB0 calls the ordinary manager before any lazy base file.
            //The render thread can supply an immutable registry snapshot so
            //worker preparation retains that exact pre-existing prototype.
            if(registeredPrototypes?.TryGetValue(requestedObjectName,out var registeredBase)==true)
            {
                if(registeredBase.AggregatePrototype is {} registeredAggregate)
                {
                    registeredInput=registeredBase;
                    logicalFileName=registeredBase.LogicalFileName;location=registeredBase.ResourceLocation;loosePath=registeredBase.LooseFilePath;
                    bytes=registeredAggregate.AggregateBytes.ToArray();
                    continue;
                }
                resource=registeredBase with {RequestedObjectName=requestedObjectName};
                break;
            }
            //The base may have been registered later in this very file. Keep
            //its source location and inspect the complete table before lazy IO.
            if (publishedFiles.Any(file=>(file.Model is {} known && HasPrototype(known,requestedObjectName)) ||
                TryFindAggregate(file.Bytes,requestedObjectName,out _,out _))) continue;
            if (!TryReadNamedResource(resources,requestedObjectName,files,out logicalFileName,out bytes,out location,out loosePath,out var baseDiagnostic))
            {
                //009A3AB0 retries after009A3B00's explicit working-directory
                //file load. Unlike the ordinary manager, this uses the full
                //name, including dots. Preserve the normal provider first.
                if (TryRetryFailedBase()) continue;
                diagnostic =
                    $"0x0600 aggregate '{aggregate.Name}' base model '{aggregate.BaseModelName}' could not materialize: {baseDiagnostic ?? "unknown base-model rejection"}.";
                ReportMissing();
                return false;
            }
        }
        for(int index=wrappers.Count-1;index>=0;index--)
        {
            var wrapper=wrappers[index];
            var inherited=resource.CurrentRetailAggregate;
            resource = new EnbResolvedW3dResource(
                wrapper.File,
                wrapper.Name,
                wrapper.Location,
                resource.Model,
                resource.Textures,
                new EnbCurrentRetailW3dAggregateResolution(
                    wrapper.Definition,
                    inherited?.BaseLogicalFileName??resource.LogicalFileName,
                    inherited?.BaseRequestedObjectName??resource.RequestedObjectName,
                    inherited?.BaseResourceLocation??resource.ResourceLocation,
                    inherited)
                    {
                        BaseRegisteredPrototype=inherited?.BaseRegisteredPrototype??resource.RegisteredPrototype,
                        BasePrototypeRegistration=inherited?.BasePrototypeRegistration??resource.PrototypeRegistration,
                        BaseLooseFilePath=inherited is null?resource.LooseFilePath:inherited.BaseLooseFilePath
                    }){LooseFilePath=wrapper.Loose,PrototypeRegistration=wrapper.Registration};
        }
        resource=resource with {AcquisitionPublications=publications.ToArray()};
        return true;
    }

    private static bool HasPrototype(EnbW3dModel model, string name)
        => model.PrototypeEntries.Any(entry => string.Equals(PrototypeName(model,entry), name, StringComparison.OrdinalIgnoreCase));

    private static string PrototypeName(EnbW3dModel model,EnbW3dPrototypeEntry entry)
        => entry.Kind switch
        {
            //009BAA73..009BAB59 forms the published mesh name.
            EnbW3dPrototypeKind.Mesh => string.IsNullOrEmpty(model.Meshes[entry.Ordinal].ContainerName)
                ? model.Meshes[entry.Ordinal].Name
                : model.Meshes[entry.Ordinal].ContainerName + "." + model.Meshes[entry.Ordinal].Name,
            EnbW3dPrototypeKind.HierarchyModel => model.HierarchyModels[entry.Ordinal].Name,
            EnbW3dPrototypeKind.HierarchicalLod => model.HierarchicalLods[entry.Ordinal].Name,
            EnbW3dPrototypeKind.Box => model.Boxes[entry.Ordinal].Name,
            EnbW3dPrototypeKind.Collection => model.Collections[entry.Ordinal].Name,
            EnbW3dPrototypeKind.LegacyLod => model.LegacyLods[entry.Ordinal].Name,
            EnbW3dPrototypeKind.Dazzle => model.Dazzles[entry.Ordinal].Name,
            EnbW3dPrototypeKind.ParticleEmitter => model.ParticleEmitters[entry.Ordinal].Name,
            EnbW3dPrototypeKind.Sphere => model.Spheres[entry.Ordinal].Header.Name,
            EnbW3dPrototypeKind.Ring => model.Rings[entry.Ordinal].Header.Name,
            EnbW3dPrototypeKind.SpecialMaterial => model.SpecialMaterials[entry.Ordinal].Name,
            EnbW3dPrototypeKind.NullObject => model.NullObjects[entry.Ordinal].Name,
            _ => throw new InvalidDataException("Unknown decoded prototype entry."),
        };

    private static bool TryFindAggregate(ReadOnlySpan<byte> bytes,string name,
        out EnbCurrentRetailW3dAggregateDefinition aggregate,out int selectedOffset)
    {
        aggregate=null!;selectedOffset=int.MaxValue;
        int offset=0;
        while(offset<bytes.Length)
        {
            if(bytes.Length-offset<8)break;
            uint kind=BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            uint length=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset+4)..])&0x7fffffffu;
            if(length>(uint)(bytes.Length-offset-8))break;
            int size=(int)length+8;
            if(kind==0x600 && aggregate is null &&
                EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes.Slice(offset,size),out var candidate,out _) &&
                string.Equals(candidate.Name,name,StringComparison.OrdinalIgnoreCase))
            { aggregate=candidate;selectedOffset=offset; }
            offset+=size;
        }
        return aggregate is not null;
    }
}
