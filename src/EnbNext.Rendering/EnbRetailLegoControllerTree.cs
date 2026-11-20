namespace EnbNext.Rendering;

/// <summary>
/// Named-controller ownership from72B7F0/72C1D0/72C8B0/72ABB0. This owns logical
/// controllers, not RenderObjects or their scene callbacks. Pending siblings
/// remain separate until replay; lookup/removal selects the first exact orphan.
/// </summary>
public sealed class EnbRetailLegoControllerTree<T> where T:class
{
    public sealed class Node
    {
        internal readonly Dictionary<string,Node> ChildrenByName=new(StringComparer.Ordinal);
        internal readonly Dictionary<string,List<Node>> PendingByName=new(StringComparer.Ordinal);
        internal readonly string[] Segments;
        public T? Value { get; set; }
        public IReadOnlyDictionary<string,Node> Children=>ChildrenByName;
        public IEnumerable<Node> Pending=>PendingByName.Values.SelectMany(v=>v);
        public IReadOnlyList<string> Path=>Segments;
        internal Node(string[] path,T? value){Segments=path;Value=value;}
    }

    public Node Root { get; }=new([],null);

    //72D0E0/72D2A0 are called in mutation order, including pending replay.
    //Consumers resolve the parent's concrete RenderObject at each call.
    public Action<Node,Node>? Attached { get; set; }
    public Action<Node,Node>? Detached { get; set; }
    //72B7F0/72C1D0 refresh only when the child entry has a mount. The
    //consumer owns that lookup.72C8B0 refreshes unconditionally after clear.
    //A nonnull child supplies the mount condition; null denotes extraction.
    public Action<Node,Node?>? RadiusRefreshRequested { get; set; }

    public static string[] ParsePath(string path)
    {
        if(path.Length==0)return [];
        var result=path.Split('/',StringSplitOptions.None);
        return path[^1]=='/'?result[..^1]:result;
    }

    public Node? Add(string path,T value)
    {
        var node=new Node(ParsePath(path),value);
        return Add(Root,node)?node:null;
    }

    public Node? Find(string path)=>Find(Root,ParsePath(path));
    public Node? Find(IReadOnlyList<string> path)=>Find(Root,path.ToArray());
    public Node? Remove(string path)=>Remove(Root,ParsePath(path));

    private static bool Split(Node parent,string[] path,out string name,out bool direct)
    {
        name=string.Empty;direct=false;
        if(path.Length<=parent.Segments.Length)return false;
        for(int i=0;i<parent.Segments.Length;i++)if(path[i]!=parent.Segments[i])return false;
        name=path[parent.Segments.Length];direct=path.Length==parent.Segments.Length+1;
        return true;
    }

    private bool Add(Node parent,Node incoming)
    {
        if(!Split(parent,incoming.Segments,out string name,out bool direct))return false;
        if(!direct)
        {
            if(parent.ChildrenByName.TryGetValue(name,out var child))return Add(child,incoming);
            if(!parent.PendingByName.TryGetValue(name,out var pending))parent.PendingByName[name]=pending=[];
            pending.Add(incoming);
            return true;
        }
        List<Node> descendants;
        if(parent.ChildrenByName.TryGetValue(name,out var previous))
        {
            descendants=Extract(previous);
            Detached?.Invoke(parent,previous);
        }
        else if(parent.PendingByName.Remove(name,out var pending))descendants=pending;
        else descendants=[];
        parent.ChildrenByName[name]=incoming;
        Attached?.Invoke(parent,incoming);
        RadiusRefreshRequested?.Invoke(parent,incoming);
        foreach(var descendant in descendants)Add(incoming,descendant);
        return true;
    }

    private static Node? Find(Node parent,string[] path)
    {
        if(!Split(parent,path,out string name,out bool direct))return null;
        if(parent.ChildrenByName.TryGetValue(name,out var child))return direct?child:Find(child,path);
        return !direct&&parent.PendingByName.TryGetValue(name,out var pending)
            ?pending.FirstOrDefault(n=>n.Segments.SequenceEqual(path)):null;
    }

    private Node? Remove(Node parent,string[] path)
    {
        if(!Split(parent,path,out string name,out bool direct))return null;
        if(parent.ChildrenByName.TryGetValue(name,out var child))
        {
            if(!direct)return Remove(child,path);
            var descendants=Extract(child);
            if(descendants.Count!=0)parent.PendingByName[name]=descendants;
            Detached?.Invoke(parent,child);
            RadiusRefreshRequested?.Invoke(parent,child);
            parent.ChildrenByName.Remove(name);
            return child;
        }
        if(direct||!parent.PendingByName.TryGetValue(name,out var pending))return null;
        int index=pending.FindIndex(n=>n.Segments.SequenceEqual(path));
        if(index<0)return null;
        var removed=pending[index];pending.RemoveAt(index);
        if(pending.Count==0)parent.PendingByName.Remove(name);
        return removed;
    }

    private List<Node> Extract(Node parent)
    {
        var result=new List<Node>();
        foreach(var child in parent.ChildrenByName.Values)
        {
            result.AddRange(Extract(child));
            Detached?.Invoke(parent,child);
            result.Add(child);
        }
        foreach(var pending in parent.PendingByName.Values)result.AddRange(pending);
        parent.ChildrenByName.Clear();parent.PendingByName.Clear();
        RadiusRefreshRequested?.Invoke(parent,null);
        return result;
    }
}
