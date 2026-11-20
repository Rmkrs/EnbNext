namespace EnbNext.Assets;

/// <summary>A single acquisition operation's authoritative FILE publications.
/// Reusing the same decoded publication during selection does not construct it
/// again; a fresh FILE decode is a new operation and still runs constructors.</summary>
public sealed class EnbCurrentRetailW3dFilePublicationContext(Func<EnbResolvedW3dResource,EnbResolvedW3dResource> publish,
    Func<string,EnbResolvedW3dResource?>? registeredSource=null)
{
    private readonly Dictionary<EnbResolvedW3dResource,EnbResolvedW3dResource> _published=new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<EnbCurrentRetailW3dPrototypeFile> _boundFiles=new(ReferenceEqualityComparer.Instance);
    public int PublicationCount=>_published.Count;
    public EnbResolvedW3dResource? FindRegisteredSource(string name)=>registeredSource?.Invoke(name);
    public EnbCurrentRetailW3dPrototypeNamespace CreateNamespace(IReadOnlyDictionary<string,EnbResolvedW3dResource>? names=null)
        => new(names,this);
    public EnbResolvedW3dResource Publish(EnbResolvedW3dResource source)
    {
        if(_published.TryGetValue(source,out var bound))return bound;
        if(source.PrototypeFile is {} file && _boundFiles.Contains(file))return source;
        bound=publish(source);_published.Add(source,bound);
        if(bound.PrototypeFile is {} boundFile)_boundFiles.Add(boundFile);
        return bound;
    }
}

/// <summary>Explicit operation context carried with its captured NAME table;
/// ordinary dictionaries remain pure/contextless preparation.</summary>
public sealed class EnbCurrentRetailW3dPrototypeNamespace : Dictionary<string,EnbResolvedW3dResource>
{
    public EnbCurrentRetailW3dFilePublicationContext? PublicationContext {get;}
    public EnbCurrentRetailW3dPrototypeNamespace(IReadOnlyDictionary<string,EnbResolvedW3dResource>? names,
        EnbCurrentRetailW3dFilePublicationContext? publicationContext=null):base(StringComparer.OrdinalIgnoreCase)
    {
        PublicationContext=publicationContext??(names as EnbCurrentRetailW3dPrototypeNamespace)?.PublicationContext;
        if(names is not null)foreach(var pair in names)Add(pair.Key,pair.Value);
    }
}
