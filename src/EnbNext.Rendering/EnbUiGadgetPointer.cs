namespace EnbNext.Rendering;

/// <summary>A resolved retail input frame: event identity/flags and current button state.
/// 0065F780 skips queued modifier-key events before constructing this frame. Hosts translate
/// their input identities; they do not choose button actions or capture ownership.</summary>
public readonly record struct EnbUiPointerFrame(ushort Code, ushort Modifiers, bool LeftDown, bool RightDown)
{
    public uint GadgetFlags
    {
        get
        {
            uint flags = Code switch
            {
                0 => 0,
                1 => (Modifiers & 0x1000) != 0 ? 4u : 1u,
                2 => (Modifiers & 0x1000) != 0 ? 0x40u : 0x10u,
                0x101 or 0x102 => 0x1000,
                _ => 0x100,
            };
            if (Code != 0) flags |= (uint)(Modifiers & 0x700) << 1;
            if (Code is 0 or 1 or 2 or 0x10 or 0x11 or 0x12)
                flags |= (LeftDown ? 2u : 8u) | (RightDown ? 0x20u : 0x80u);
            return flags;
        }
    }
}

[Flags]
public enum EnbUiCaptureChange { None = 0, TakeLeft = 1, ReleaseLeft = 2, TakeRight = 4, ReleaseRight = 8 }
public readonly record struct EnbUiGadgetResponse(bool Consumed, bool Rewritten = false,
    EnbUiCaptureChange Capture = EnbUiCaptureChange.None, bool? Selected = null);

/// <summary>Retail gadget virtual handler boundary. A false TryHandle selects the
/// standard button/checkbox/background handler. Flags precede shared-event rewriting.</summary>
public interface IEnbUiGadgetHandler
{
    bool TryHandle(int index, ref EnbUiPointerFrame frame, uint flags, out EnbUiGadgetResponse response);
    bool HasFlash(int index);
    void CaptureAcquired(int index) { }
    void CaptureReleased(int index) { }
    void Retired(int index) { }
    // 0065FC8C: enabled mask100 gadgets receive keys even outside hit geometry.
    bool AcceptsKeyboard(int index) => false;
    // 006609A0 queries the old hit source before normal traversal, but captured
    // gadgets have already performed their current hit query.
    int FlashSource(int index, bool beforeHitQuery) => HasFlash(index) ? 0 : -1;
}

/// <summary>Retail ordered gadget dispatch (0065F780), independent of its panel.
/// An optional final gadget is the background; other indices follow established native order.</summary>
public sealed class EnbUiGadgetPointer
{
    private readonly ulong checkboxMask;
    private readonly int commandCount, backgroundIndex;
    internal int GadgetCount => gadgets.Length;
    internal EnbUiGadgetGroup Group;
    internal int GroupPriority;
    private readonly IEnbUiGadgetHandler? custom;
    public EnbUiGadgetPointer(int buttonCount, ulong checkboxMask = 0, IEnbUiGadgetHandler? custom = null, bool hasBackground = true)
    {
        if (buttonCount < 0) throw new ArgumentOutOfRangeException(nameof(buttonCount));
        commandCount = buttonCount; backgroundIndex = hasBackground ? buttonCount : -1;
        gadgets = new Gadget[buttonCount + (hasBackground ? 1 : 0)];
        this.checkboxMask = checkboxMask; this.custom = custom;
    }
    private struct Gadget
    {
        public bool NextHit, Hit, FirstHit, FirstEnabled, Selected, Flash, FlashWasInside, Disabled;
        public int FlashSource;
    }
    private readonly Gadget[] gadgets;
    private EnbUiGadgetContext? context;
    public EnbUiGadgetContext CaptureContext => Context;
    internal EnbUiGadgetContext Context => context ??= EnbUiGadgetContext.For(this);
    internal void SetContext(EnbUiGadgetContext? value) => context = value;
    internal EnbUiGadgetContext? AttachedContext => context;
    // 006AB5B0 -> 0065F5F0 -> 00661AA0: first-hit flag first, then any hit.
    // Captured gadgets retain their hit flags while normal traversal skips them.
    public int TooltipGadget
    {
        get
        {
            for (int i = 0; i < gadgets.Length; i++) if (gadgets[i].Hit && gadgets[i].FirstHit) return i;
            for (int i = 0; i < gadgets.Length; i++) if (gadgets[i].Hit) return i;
            return -1;
        }
    }
    // Native +68/+6c capture virtuals. Base gadgets change only the capture slot;
    // overrides (such as the editor row colors) receive their own notification.
    public bool AcquireCapture(int index, uint flags)
    {
        if ((uint)index >= gadgets.Length) throw new ArgumentOutOfRangeException(nameof(index));
        bool acquired=Context.Acquire(this,index,flags);
        if(acquired)custom?.CaptureAcquired(index);
        return acquired;
    }
    public void ReleaseCapture(int index, uint flags)
    {
        if ((uint)index >= gadgets.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Context.Release(this,index,flags);
        custom?.CaptureReleased(index);
    }

    public int LeftCapture => context?.Left.IndexFor(this) ?? -1;
    public int RightCapture => context?.Right.IndexFor(this) ?? -1;
    public bool Selected(int index) => gadgets[index].Selected;
    public void SetSelected(int index, bool selected) => gadgets[index].Selected = selected;
    public bool Flash(int index) => gadgets[index].Flash;
    public bool FlashInside(int index) => gadgets[index].FlashWasInside;
    public int FlashSource(int index) => gadgets[index].Flash ? gadgets[index].FlashSource : -1;
    public bool Hit(int index) => gadgets[index].Hit;
    public bool FirstEnabled(int index) => gadgets[index].FirstEnabled;
    public int LastHandler { get; private set; } = -1;
    public bool Consumed { get; private set; }

    // 007E7FC0 toggles even when modifiers prevent the interface command match.
    public int ToggleHandler { get; private set; } = -1;

    public void Retire()
    {
        Array.Clear(gadgets);
        context?.Retire(this);
        custom?.Retired(-1);
        LastHandler = ToggleHandler = -1;
        Consumed = false;
    }

    /// <summary>00660140 removes one child without retiring its siblings' captures.</summary>
    public void Retire(int index)
    {
        gadgets[index] = default;
        context?.Retire(this, index);
        custom?.Retired(index);
        if (LastHandler == index) LastHandler = -1;
        if (ToggleHandler == index) ToggleHandler = -1;
    }

    /// <returns>The bound command, only for a rewritten, unmodified event.
    /// Modifier and release bits survive 0065F4B0 and fail the complete-word binding.</returns>
    public int Dispatch(EnbUiPointerFrame frame, uint hitMask)
    {
        if (gadgets.Length > 32) throw new InvalidOperationException("Use ordered hit results for panels with more than 31 buttons.");
        Span<bool> hits = stackalloc bool[32];
        for (int i = 0; i < gadgets.Length; i++) hits[i] = (hitMask & (1u << i)) != 0;
        return Dispatch(frame, hits[..gadgets.Length]);
    }

    /// <summary>Hit results in dispatch order, including the final background gadget.</summary>
    public int Dispatch(EnbUiPointerFrame frame, ReadOnlySpan<bool> hits) => Dispatch(ref frame, hits);

    /// <summary>Returns the same rewritten event to the interface command consumer.</summary>
    public int Dispatch(ref EnbUiPointerFrame frame, ReadOnlySpan<bool> hits)
    {
        if (Context.Count != 1) throw new InvalidOperationException("Prepare all active groups and dispatch their shared context once.");
        Prepare(hits);
        Context.Dispatch(ref frame);
        return Command(frame);
    }

    public void DispatchPrepared(ref EnbUiPointerFrame frame)
    {
        if (Context.Count != 1) throw new InvalidOperationException("Dispatch the shared context once after preparing all groups.");
        Context.Dispatch(ref frame);
    }

    public void Prepare(uint hitMask)
    {
        if (gadgets.Length > 32) throw new InvalidOperationException("Use ordered hit results for panels with more than 31 buttons.");
        for (int i = 0; i < gadgets.Length; i++) gadgets[i].NextHit = (hitMask & (1u << i)) != 0;
    }

    public void Prepare(ReadOnlySpan<bool> hits)
    {
        if (hits.Length != gadgets.Length) throw new ArgumentException("One hit result is required per gadget.", nameof(hits));
        for (int i = 0; i < gadgets.Length; i++) gadgets[i].NextHit = hits[i];
    }

    //0065F640: disable retires capture/flash directly, without focus-release callbacks.
    public void SetEnabled(int index,bool enabled)
    {
        ref var g=ref gadgets[index];g.Disabled=!enabled;
        if(enabled)return;
        Context.ReleaseLeft(this,index);Context.ReleaseRight(this,index);g.Flash=false;
    }
    public bool Enabled(int index)=>!gadgets[index].Disabled;

    public int Command(EnbUiPointerFrame frame) => LastHandler >= 0 && LastHandler < commandCount && frame.Modifiers == 0 ? LastHandler : -1;
    internal void BeginFrame() { LastHandler = ToggleHandler = -1; Consumed = false; }
    internal void FinishFrame(bool consumed) => Consumed = consumed;
    internal int FindTooltip(bool firstOnly, int excluded)
    {
        for (int i = 0; i < gadgets.Length; i++) if (i != excluded && gadgets[i].Hit && (!firstOnly || gadgets[i].FirstHit)) return i;
        return -1;
    }
    internal void Traverse(ref EnbUiPointerFrame frame, uint flags, EnbUiGadgetRef captured, ref EnbUiGadgetDispatch state)
    {
        ref bool firstHit = ref state.FirstHit;
        ref bool firstEnabled = ref state.FirstEnabled;
        for (int i = 0; i < gadgets.Length; i++)
        {
            ref Gadget gadget = ref gadgets[i];
            // 006609A0 runs before this frame's hit/first-enabled mutations.
            if (gadget.Flash)
            {
                if (gadget.Hit && gadget.FirstEnabled) gadget.FlashWasInside = true;
                else if (gadget.FlashWasInside) gadget.Flash = false;
                if (gadget.Flash && custom is not null)
                {
                    int source = custom.FlashSource(i, !captured.Is(this, i));
                    if (source < 0) gadget.Flash = false;
                    else if (source != gadget.FlashSource)
                    {
                        gadget.FlashSource = source;
                        gadget.FlashWasInside = gadget.Hit && gadget.FirstEnabled;
                    }
                }
            }
            if (captured.Is(this, i)) continue;
            gadget.Hit = gadget.NextHit;
            if (!gadget.Hit)
            {
                if (!gadget.Disabled && state.Consumed.Owner is null && (flags & 0x100) != 0 && custom?.AcceptsKeyboard(i) == true &&
                    Handle(i, ref frame, flags)) state.Consumed = new(this, i);
                continue;
            }
            gadget.FirstHit = !firstHit;
            if (!firstHit) { firstHit = true; state.PreviousTop = new(this, i); }
            gadget.FirstEnabled = false;
            if (state.Consumed.Owner is not null || gadget.Disabled) continue;
            if (!firstEnabled)
            {
                firstEnabled = gadget.FirstEnabled = true;
                if (state.OldTop.Is(this, i) && !gadget.Flash) CreateFlash(i);
            }
            if (Handle(i, ref frame, flags))
            {
                state.Consumed = new(this, i);
                if (gadget.Flash) gadget.Flash = false;
                else CreateFlash(i);
            }
        }
    }

    private void CreateFlash(int index)
    {
        // 00674260 clears the background's flash source. 006603A0 immediately
        // updates a new clone, so inside status is initialized in the same frame.
        if (index == backgroundIndex) return;
        int source = custom?.FlashSource(index, false) ?? 0;
        if (source < 0) return;
        ref Gadget gadget = ref gadgets[index];
        gadget.FlashSource = source;
        gadget.Flash = true;
        gadget.FlashWasInside = gadget.Hit && gadget.FirstEnabled;
    }

    internal bool Handle(int index, ref EnbUiPointerFrame frame, uint flags)
    {
        if (custom is not null && custom.TryHandle(index, ref frame, flags, out var result))
        {
            if (result.Rewritten) LastHandler = index;
            if (result.Selected is { } selected) gadgets[index].Selected = selected;
            if ((result.Capture & EnbUiCaptureChange.TakeLeft) != 0) Context.TakeLeft(this, index);
            if ((result.Capture & EnbUiCaptureChange.TakeRight) != 0) Context.TakeRight(this, index);
            if ((result.Capture & EnbUiCaptureChange.ReleaseLeft) != 0 && LeftCapture == index) Context.ReleaseLeft(this, index);
            if ((result.Capture & EnbUiCaptureChange.ReleaseRight) != 0 && RightCapture == index) Context.ReleaseRight(this, index);
            return result.Consumed;
        }
        bool background = index == backgroundIndex;
        bool checkbox = index < 64 && (checkboxMask & (1ul << index)) != 0;
        flags &= background ? 0x10ffu : checkbox ? 0x11u : 0x55u; // 0065F500
        if (flags == 0) return false;
        if (background) { LastHandler = index; return true; } // base handler
        ref Gadget gadget = ref gadgets[index];
        if ((flags & 0x11) != 0)
        {
            LastHandler = index;
            // Mask11 has no release bits: 0066B910 skips capture and selection.
            if (checkbox) { ToggleHandler = index; return true; }
            // 006601E0: duplicate capture clears the occupied slot (assert path).
            if ((flags & 0x10) != 0) Context.TakeRight(this, index);
            else Context.TakeLeft(this, index);
            gadget.Selected = true;
            return true;
        }
        if (((flags & 4) != 0 && LeftCapture == index) || ((flags & 0x40) != 0 && RightCapture == index))
        {
            gadget.Selected = false;
            LastHandler = index;
            if ((flags & 0x40) != 0) { if (RightCapture == index) Context.ReleaseRight(this, index); }
            else if (LeftCapture == index) Context.ReleaseLeft(this, index);
            return true;
        }
        return false;
    }
}
