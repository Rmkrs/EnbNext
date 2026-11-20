using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbConfirmationPanel : IEnbUiGadgetHandler
{
    // First sort group: reverse equal-priority options, then the outer window.
    public const int InputCount = 3;
    private EnbDynamicWindowGadget noInput = new(true), yesInput = new(true), outerInput = new(false);
    private EnbUiGadgetPointer? input;
    private int inputOffset;
    private EnbUiGadgetPointer? noPointer,yesPointer,outerPointer;
    private EnbUiGadgetContext? standaloneContext;
    private EnbUiGadgetRef InputAt(int index)=>standaloneContext is not null
        ?new(index==0?noPointer:index==1?yesPointer:outerPointer,0):new(input,inputOffset+index);
    private bool InputSelected(int index){var r=InputAt(index);return r.Owner?.Selected(r.Index)==true;}
    private bool InputBound=>input is not null || standaloneContext is not null;
    private bool InputHandled=>standaloneContext is not null
        ?noPointer!.LastHandler>=0 || yesPointer!.LastHandler>=0 || outerPointer!.LastHandler>=0:input?.LastHandler>=0;
    public void AttachInput(EnbUiGadgetContext context)
    {
        if(standaloneContext==context)return;
        DetachInput();RetireInput();standaloneContext=context;
        noPointer??=new(1,custom:this,hasBackground:false);
        yesPointer??=new(1,custom:this,hasBackground:false);
        outerPointer??=new(1,custom:this,hasBackground:false);
        context.Add(noPointer,EnbUiGadgetGroup.First,-1002);
        context.Add(yesPointer,EnbUiGadgetGroup.First,-1002);
        context.Add(outerPointer,EnbUiGadgetGroup.First,0);
    }
    public void DetachInput()
    {
        if(standaloneContext is not {} context)return;
        RetireInput();context.Remove(noPointer!);context.Remove(yesPointer!);context.Remove(outerPointer!);standaloneContext=null;
    }
    public void PrepareInput(EnbConfirmationState state,EnbUiSize size,double scaling,float x,float y)
    {
        Span<bool> hits=stackalloc bool[InputCount];PrepareInput(state,size,scaling,x,y,hits);
        noPointer!.Prepare(hits[0]?1u:0u);yesPointer!.Prepare(hits[1]?1u:0u);outerPointer!.Prepare(hits[2]?1u:0u);
    }
    public int CompleteInput(EnbUiPointerFrame frame)
    {
        int command=-1;
        for(int i=0;i<InputCount;i++){var r=InputAt(i);if(r.Owner!.Command(frame)==r.Index){command=inputOffset+i;break;}}
        return CompleteInput(command,frame);
    }
    bool IEnbUiGadgetHandler.HasFlash(int index)=>false;
    bool IEnbUiGadgetHandler.TryHandle(int index,ref EnbUiPointerFrame frame,uint flags,out EnbUiGadgetResponse response)
    {response=default;return false;}

    private uint selectedBefore;
    private (ushort Code, ushort Modifiers) yesKey, noKey;
    private bool inputVisible;
    public event Action<string>? SoundRequested;

    public void BindInput(EnbUiGadgetPointer owner, int offset)
    {
        DetachInput();
        if (ReferenceEquals(input, owner) && inputOffset == offset) return;
        RetireInput(); input = owner; inputOffset = offset;
    }

    internal void RetireInput()
    {
        for (int i = 0; i < InputCount; i++){var r=InputAt(i);r.Owner?.Retire(r.Index);}
        noInput.Reset(); yesInput.Reset(); outerInput.Reset();
        selectedBefore = 0; inputVisible = false; yesKey = noKey = default;
    }

    private void PrepareKeys(IReadOnlyDictionary<string, string> definition)
    {
        yesKey = EnbAuthoredKey.Dialog(definition.GetValueOrDefault("Key0", ""));
        noKey = options.Length > 1 ? EnbAuthoredKey.Dialog(definition.GetValueOrDefault("Key1", "")) : default;
        inputVisible = true;
    }

    public void PrepareInput(EnbConfirmationState state, EnbUiSize size, double scaling,
        float normalizedX, float normalizedY, Span<bool> hits)
    {
        if (hits.Length != InputCount) throw new ArgumentException("Three ordered dialog hits required.", nameof(hits));
        hits.Clear();
        if (!Prepare(state, size, scaling)) return;
        if (!InputBound) throw new InvalidOperationException("Bind the parent gadget owner before dispatch.");
        selectedBefore = 0;
        for (int i = 0; i < InputCount; i++)
            if (InputSelected(i)) selectedBefore |= 1u << i;
        if (options.Length > 1) hits[0] = noInput.Query(Bounds[3], normalizedX, normalizedY);
        hits[1] = yesInput.Query(Bounds[2], normalizedX, normalizedY);
        hits[2] = outerInput.Query(Bounds[0], normalizedX, normalizedY);
    }

    public int CompleteInput(int command, EnbUiPointerFrame frame)
    {
        if (!inputVisible || !InputBound) return 0;
        noInput.SelectionChanged((selectedBefore & 1) != 0, InputSelected(0));
        yesInput.SelectionChanged((selectedBefore & 2) != 0, InputSelected(1));
        outerInput.SelectionChanged((selectedBefore & 4) != 0, InputSelected(2));
        int result = command == inputOffset && options.Length > 1 ? 2 : command == inputOffset + 1 ? 1 : 0;
        if (result == 0 && !InputHandled && frame.Code != 0)
        {
            if (yesKey.Code != 0 && (frame.Code, frame.Modifiers) == yesKey) result = 1;
            else if (noKey.Code != 0 && (frame.Code, frame.Modifiers) == noKey) result = 2;
        }
        if (result != 0) SoundRequested?.Invoke("Roll_Select02b.wav");
        return result;
    }

    private float InputEmissive(int frameIndex) => frameIndex switch
    { 0 => outerInput.Emissive, 2 => yesInput.Emissive, 3 => noInput.Emissive, _ => 1 };
}
