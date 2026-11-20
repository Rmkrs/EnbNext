namespace EnbNext.Rendering;

public enum EnbUiGadgetGroup { Ordinary, First, Last }

/// <summary>Identity in the actual panel-owned gadget array; no copied gadget state.</summary>
public readonly record struct EnbUiGadgetRef(EnbUiGadgetPointer? Owner, int Index)
{
    internal bool Is(EnbUiGadgetPointer owner, int index) => Owner == owner && Index == index;
    internal int IndexFor(EnbUiGadgetPointer owner) => Owner == owner ? Index : -1;
}

internal struct EnbUiGadgetDispatch
{
    public EnbUiGadgetRef Consumed, OldTop, PreviousTop;
    public bool FirstHit, FirstEnabled;
}

/// <summary>00661BB0/00661DA0: newest ordinary interface group first, each in its
/// established gadget order. 0065F780 shares capture and one mutable event across groups.</summary>
public sealed class EnbUiGadgetContext
{
    private EnbUiGadgetPointer?[] groups = new EnbUiGadgetPointer?[4];
    private int count;
    private EnbUiGadgetRef previousTop, tooltipExcluded;
    internal EnbUiGadgetRef Left, Right;
    public EnbUiGadgetRef LeftCapture => Left;
    public EnbUiGadgetRef RightCapture => Right;
    public int Count => count;
    public EnbUiGadgetRef ConsumedBy { get; private set; }
    internal static EnbUiGadgetContext For(EnbUiGadgetPointer owner)
    {
        var result = new EnbUiGadgetContext();
        result.groups[0] = owner; result.count = 1;
        return result;
    }

    public void Add(EnbUiGadgetPointer owner, EnbUiGadgetGroup group = EnbUiGadgetGroup.Ordinary, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.AttachedContext == this) return;
        // Reserved lists are globally priority-sorted. Register each actual reserved gadget,
        // rather than grouping a panel's unrelated priorities behind one owner.
        if (group != EnbUiGadgetGroup.Ordinary && owner.GadgetCount != 1)
            throw new ArgumentException("A reserved registration must own one actual gadget.", nameof(owner));
        owner.AttachedContext?.Remove(owner);
        owner.Retire();
        if (count == groups.Length) Array.Resize(ref groups, count * 2);
        int insertion = 0;
        // 00661BB0 preserves the first/last reserved lists around newest-first ordinary groups.
        // 00661700 inserts after existing equal priorities.
        while (insertion < count)
        {
            var existing = groups[insertion]!;
            bool before = group switch
            {
                EnbUiGadgetGroup.First => existing.Group == EnbUiGadgetGroup.First && existing.GroupPriority <= priority,
                EnbUiGadgetGroup.Last => existing.Group != EnbUiGadgetGroup.Last || existing.GroupPriority <= priority,
                _ => existing.Group == EnbUiGadgetGroup.First
            };
            if (!before) break;
            insertion++;
        }
        owner.Group = group; owner.GroupPriority = priority;
        Array.Copy(groups, insertion, groups, insertion + 1, count - insertion);
        groups[insertion] = owner; count++;
        owner.SetContext(this);
    }

    public void Remove(EnbUiGadgetPointer owner)
    {
        if (owner.AttachedContext != this) return;
        int index = Array.IndexOf(groups, owner, 0, count);
        owner.Retire();
        Array.Copy(groups, index + 1, groups, index, count - index - 1);
        groups[--count] = null;
        owner.SetContext(null);
    }

    internal void Retire(EnbUiGadgetPointer owner, int index = -1)
    {
        if (Left.Owner == owner && (index < 0 || Left.Index == index)) Left = default;
        if (Right.Owner == owner && (index < 0 || Right.Index == index)) Right = default;
        if (previousTop.Owner == owner && (index < 0 || previousTop.Index == index)) previousTop = default;
        if (ConsumedBy.Owner == owner && (index < 0 || ConsumedBy.Index == index)) ConsumedBy = default;
    }
    //006602C0 invokes the actual release virtual, then re-reads right capture.
    public void CancelCaptures(uint flags)
    {
        if (Left.Owner is { } left) left.ReleaseCapture(Left.Index, flags);
        if (Right.Owner is { } right) right.ReleaseCapture(Right.Index, flags);
    }
    internal bool Acquire(EnbUiGadgetPointer owner, int index, uint flags)
    {
        if ((flags & 0x10) == 0 && (flags & 0xf) != 0)
        { TakeLeft(owner,index); return Left.Is(owner,index); }
        if ((flags & 0xf0) != 0)
        { TakeRight(owner,index); return Right.Is(owner,index); }
        return false;
    }
    internal void Release(EnbUiGadgetPointer owner, int index, uint flags)
    {
        if ((flags & 0x40) == 0 && (flags & 0xf) != 0) ReleaseLeft(owner,index);
        else if ((flags & 0xf0) != 0) ReleaseRight(owner,index);
    }

    internal void TakeLeft(EnbUiGadgetPointer owner, int index) => Left = Left.Owner is null ? new(owner, index) : default;
    internal void TakeRight(EnbUiGadgetPointer owner, int index) => Right = Right.Owner is null ? new(owner, index) : default;
    internal void ReleaseLeft(EnbUiGadgetPointer owner, int index) { if (Left.Is(owner, index)) Left = default; }
    internal void ReleaseRight(EnbUiGadgetPointer owner, int index) { if (Right.Is(owner, index)) Right = default; }

    public EnbUiGadgetRef Tooltip
    {
        get
        {
            // 00661AA0: first-hit across the manager before fallback to any hit.
            for (int pass = 0; pass < 2; pass++)
                for (int g = 0; g < count; g++)
                {
                    var owner = groups[g]!;
                    int index = owner.FindTooltip(pass == 0, tooltipExcluded.IndexFor(owner));
                    if (index >= 0) return new(owner, index);
                }
            return default;
        }
    }

    public void Dispatch(ref EnbUiPointerFrame frame)
    {
        uint flags = frame.GadgetFlags;
        for (int g = 0; g < count; g++) groups[g]!.BeginFrame();
        var state = new EnbUiGadgetDispatch { OldTop = previousTop };
        // 0065F9B7..0065FA22: edge, held, then up; left before right at each tier.
        bool captureLeft;
        if ((flags & 5) != 0 && Left.Owner is not null) captureLeft = true;
        else if ((flags & 0x50) != 0 && Right.Owner is not null) captureLeft = false;
        else if ((flags & 2) != 0 && Left.Owner is not null) captureLeft = true;
        else if ((flags & 0x20) != 0 && Right.Owner is not null) captureLeft = false;
        else captureLeft = (flags & 8) != 0 && Left.Owner is not null;
        var captured = captureLeft ? Left : ((flags & 0xf0) != 0 ? Right : default);
        if (captured.Owner is { } owner)
        {
            owner.Handle(captured.Index, ref frame, flags);
            var retained = captureLeft ? Left : Right;
            if (retained.Owner is not null)
            {
                state.Consumed = retained;
                state.FirstHit = state.FirstEnabled = true;
            }
        }
        for (int g = 0; g < count; g++) groups[g]!.Traverse(ref frame, flags, captured, ref state);
        // 0065F780 builds the tooltip hit list only in normal traversal, excluding
        // the captured gadget even though its retained hit flags remain set.
        tooltipExcluded = captured;
        previousTop = state.PreviousTop;
        ConsumedBy = state.Consumed;
        for (int g = 0; g < count; g++) groups[g]!.FinishFrame(state.Consumed.Owner == groups[g]);
    }
}
