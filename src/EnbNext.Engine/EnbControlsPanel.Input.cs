using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbControlsPanel : IEnbUiGadgetHandler
{
    private const int DialogCount = EnbConfirmationPanel.InputCount;
    private const int KeyboardIndex = DialogCount, SliderIndex = DialogCount + 1;
    private EnbUiGadgetPointer? nativePointer;
    public EnbUiGadgetPointer Pointer => nativePointer ??= new(resources.PointerOrder.Length + DialogCount - 1, custom: this);
    public EnbKeyboardGadget Keyboard { get; } = new();
    private EnbConfirmationPanel? dialogPanel;
    private EnbUiPoint inputPoint;
    private long inputTime;
    private ushort heldModifiers;
    private bool inputModal, inputCapturing;
    private EnbScrollbarPart scrollHit, previousScrollHit;
    private readonly EnbAttachedFontColor[] nativeClocks = new EnbAttachedFontColor[resources.Labels.Length];
    private readonly EnbRgb[] nativeInks = new EnbRgb[resources.Labels.Length];
    private readonly bool[] nativeLabelHover = new bool[resources.Labels.Length];

    public void BindDialog(EnbConfirmationPanel panel)
    {
        if (ReferenceEquals(dialogPanel, panel)) return;
        if (dialogPanel is not null) dialogPanel.SoundRequested -= DialogSound;
        dialogPanel = panel; panel.SoundRequested += DialogSound; panel.BindInput(Pointer, 0);
    }
    private void DialogSound(string sound) => SoundRequested?.Invoke(sound);

    public string? InputKey(int index) => index switch
    {
        0 => EnbConfirmationPanel.Key(3), 1 => EnbConfirmationPanel.Key(2), 2 => EnbConfirmationPanel.Key(0),
        >= DialogCount when index - DialogCount < resources.PointerOrder.Length => resources.PointerOrder[index - DialogCount],
        _ => null,
    };

    public bool Contains(float x, float y)
    {
        if (Draft is null) return false;
        Span<bool> hits = stackalloc bool[resources.PointerOrder.Length];
        PointerHits(hits, Rows, ScrollInput.Offset, false, false, x, y);
        foreach (bool hit in hits) if (hit) return true;
        var point = new EnbUiPoint(x * 640d, y * 480d);
        return confirmation.Text is not null && dialogPanel is not null && dialogPanel.Bounds[0].Contains(point);
    }

    public void CancelInput()
    {
        nativePointer?.Retire(); dialogPanel?.RetireInput(); ScrollInput.Cancel();
        scrollHit = previousScrollHit = EnbScrollbarPart.None;
    }

    private void ResetNativeColors()
    {
        Array.Clear(nativeClocks); Array.Clear(nativeLabelHover);
        for (int i = 0; i < nativeInks.Length; i++)
            nativeInks[i] = palette.ResolveSelected(resources.Labels[i].Definition.Properties, false, false, 0);
    }

    public EnbControlsCommandResult AdvanceInput(EnbUiInputQueue queue, EnbUiSize size, double scaling, long now)
    {
        if (Draft is null) return default;
        if (dialogPanel is null) throw new InvalidOperationException("Controls require their shared confirmation owner.");
        var input = queue.TakeFrame();
        PrepareInput(input, queue.HeldModifiers, size, scaling, now);
        var frame = new EnbUiPointerFrame(input.Code, input.Modifiers, queue.LeftDown, queue.RightDown);
        Pointer.DispatchPrepared(ref frame);
        return CompleteInput(frame, queue, size, scaling, now);
    }

    public void PrepareInput(EnbUiInputEvent input, ushort modifiers, EnbUiSize size, double scaling, long now)
    {
        if (dialogPanel is null) throw new InvalidOperationException("Controls require their shared confirmation owner.");
        ScrollInput.Configure(Rows.Length, 11);
        float x = Math.Clamp(input.X, 0, 1), y = Math.Clamp(input.Y, 0, 1);
        inputPoint = new(x * 640d, y * 480d); inputTime = now; heldModifiers = modifiers;
        previousScrollHit = scrollHit;
        scrollHit = ScrollInput.Hit(inputPoint);
        bool wheel = input.Code is 0x101 or 0x102;
        bool sliderHit = wheel ? resources.WheelBounds is { } bounds && bounds.Contains(inputPoint) : scrollHit != EnbScrollbarPart.None;
        if (wheel) scrollHit = EnbScrollbarPart.None; // native region6 has no flash source
        Span<bool> hits = stackalloc bool[resources.PointerOrder.Length + DialogCount];
        inputModal = confirmation.Text is not null;
        inputCapturing = Capture.Action is not null && Keyboard.Enabled;
        dialogPanel.PrepareInput(confirmation, size, scaling, x, y, hits[..DialogCount]);
        PointerHits(hits[DialogCount..], Rows, ScrollInput.Offset, Keyboard.Enabled, sliderHit, x, y);
        Pointer.Prepare(hits);
    }

    public EnbControlsCommandResult CompleteInput(EnbUiPointerFrame frame, EnbUiInputQueue queue, EnbUiSize size, double scaling, long now)
    {
        if (Draft is null || dialogPanel is null) return default;
        int command = Pointer.Command(frame);
        bool modal = inputModal, capturing = inputCapturing;
        int answer = dialogPanel.CompleteInput(command, frame);
        EnbControlsCommandResult result = default;
        if (capturing && !Keyboard.Enabled && !modal)
            Capture.Complete(Draft, Keyboard.Code, Keyboard.Modifiers);
        if (answer != 0) { Capture.Answer(Draft, answer == 1); Keyboard.Cancel(); dialogPanel.Prepare(confirmation, size, scaling); }
        if (!modal)
        {
            if (command == SliderIndex)
            {
                Capture.Reset(); Keyboard.Cancel();
            }
            else if (command >= DialogCount + 2 && command < resources.PointerOrder.Length + DialogCount - 1)
            {
                Keyboard.Cancel(); result = Activate(InputKey(command)!);
                if (Capture.Action is not null) Keyboard.Begin();
            }
            else if (command < 0 && frame.Code == 0x1b && frame.Modifiers == 0)
            { Keyboard.Cancel(); result = Activate("control/EXIT"); }
        }
        int width = Math.Max(1, (int)Math.Round(size.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(size.Height * scaling));
        for (int i = 0; i < resources.Labels.Length; i++)
        {
            var binding = resources.Labels[i];
            bool inside = EnbAttachedFontColor.Contains(binding.Style.Clip, width, height, queue.X, queue.Y);
            nativeLabelHover[i] = inside;
            bool selected = binding.GadgetIndex >= 0 && Pointer.Selected(binding.GadgetIndex + DialogCount);
            if (Keyboard.Enabled && IsCaptureLabel(binding.Attach)) selected = true;
            nativeInks[i] = nativeClocks[i].Update(palette, binding.Definition.Properties, inside, selected, now);
        }
        return result;
    }

    private bool IsCaptureLabel(string attach)
    {
        if (!attach.Contains("_KEY") || !int.TryParse(attach.AsSpan(attach.Length - 2), out int row)) return false;
        int index = ScrollInput.Offset + row;
        return index < Rows.Length && Capture.Action == Rows[index].Action && Capture.Slot == (attach.StartsWith("PRIME") ? 1 : 2);
    }

    bool IEnbUiGadgetHandler.HasFlash(int index) => index > KeyboardIndex;
    int IEnbUiGadgetHandler.FlashSource(int index, bool beforeHitQuery)
    {
        if (index <= KeyboardIndex) return -1;
        if (index != SliderIndex) return 0;
        var part = beforeHitQuery ? previousScrollHit : scrollHit;
        return part is EnbScrollbarPart.Up or EnbScrollbarPart.Thumb or EnbScrollbarPart.Down ? (int)part : -1;
    }

    bool IEnbUiGadgetHandler.TryHandle(int index, ref EnbUiPointerFrame frame, uint flags, out EnbUiGadgetResponse response)
    {
        response = default;
        if (index == KeyboardIndex)
        { response = new(Keyboard.Handle(ref frame, flags, heldModifiers)); return true; }
        if (index != SliderIndex) return false;
        flags &= 0x1077;
        if (flags == 0) return true;
        bool captured = Pointer.LeftCapture == index || Pointer.RightCapture == index;
        var capture = EnbUiCaptureChange.None;
        bool rewritten = false;
        if ((flags & 0x11) != 0)
        {
            rewritten = true;
            capture = (flags & 0x10) != 0 ? EnbUiCaptureChange.TakeRight : EnbUiCaptureChange.TakeLeft;
            ScrollInput.Begin(scrollHit, inputPoint, inputTime); ScrollCue();
        }
        else if (captured && (flags & 0x44) != 0)
        {
            rewritten = true;
            capture = (flags & 0x40) != 0 ? EnbUiCaptureChange.ReleaseRight : EnbUiCaptureChange.ReleaseLeft;
            ScrollInput.Cancel();
        }
        else if (captured && (flags & 0x22) != 0)
        {
            rewritten = true;
            if (ScrollInput.Move(inputPoint) || ScrollInput.Advance(inputPoint, inputTime)) ScrollCue();
        }
        else if (!captured && (flags & 0x1000) != 0)
        {
            rewritten = true; ScrollInput.SetValue(ScrollInput.Value + (frame.Code == 0x101 ? -1 : 1)); ScrollCue();
        }
        response = new(true, rewritten, capture); return true;
    }

    private void ScrollCue()
    { if (ScrollInput.NotchChanged) SoundRequested?.Invoke("slider_bar_1.wav"); }

    public ImmutableList<EnbUiModelDraw> DrawFrame(EnbUiSize size, double scaling, long now, string? error = null)
    {
        var draws = DrawCore(confirmation.Text is not null, size, scaling, null, null, now, error, true);
        return confirmation.Text is not null && dialogPanel is not null
            ? draws.AddRange(dialogPanel.DrawFrame(confirmation, size, scaling)) : draws;
    }
}
