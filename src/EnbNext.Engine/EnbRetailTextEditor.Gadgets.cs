using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

public sealed partial class EnbRetailTextEditor : IEnbUiGadgetHandler
{
    private EnbUiGadgetPointer? gadget;
    private EnbUiPoint gadgetPoint;
    private bool gadgetHit, gadgetObstructed;
    private char gadgetCharacter;
    public EnbUiGadgetPointer Pointer => gadget ??= new(1,custom:this,hasBackground:false);
    private bool skipCapturedInput;
    internal bool SkipCapturedInput => skipCapturedInput;
    public EnbRetailEditorKeyResult GadgetKeyResult { get; private set; }
    public event Action<string>? GadgetSoundRequested;

    // 006720A0: the linked slider suppresses this hit and latches one captured event.
    // Geometry and current slider capture are resolved by the actual panel/dispatcher.
    public bool QueryGadget(bool modelHit, bool sliderCaptured)
    {
        GadgetKeyResult = EnbRetailEditorKeyResult.None;
        if (!Multiline) return false; // 0065C920, single-line virtual hit.
        if (sliderCaptured) { skipCapturedInput = true; return false; }
        return modelHit;
    }

    public EnbUiGadgetResponse HandleGadgetEvent(ref EnbUiPointerFrame frame, uint flags,
        bool captured, bool leftAvailable, bool modelHit, bool obstructed, EnbUiPoint point, char translated = '\0')
    {
        uint captureFlags = flags;
        flags &= Multiline ? 0x105u : 0x100u; // 0065F500 and constructor masks.
        if (flags == 0) return default;
        if (!Multiline)
        {
            if(!IsFocused)return default;
            if((frame.Modifiers&0x1000)==0)
                GadgetSoundRequested?.Invoke(frame.Code is 9 or 13 or 27?"Roll_Select02b.wav":"fontclick.wav");
            GadgetKeyResult = HandleKey(frame.Code,frame.Modifiers,translated);
            //006AA200 invokes0065F4B0 for every accepted event, including key-up.
            return new(true,true);
        }
        if (!captured)
        {
            if ((flags & 1) == 0) return default;
            GadgetSoundRequested?.Invoke("fontclick.wav");
            CaretIndex = HitMultiline(point);
            if (!leftAvailable) return new(true,true);
            if (!Entered) Text = "";
            IsFocused = true;
            return new(true,true,EnbUiCaptureChange.TakeLeft);
        }
        if (skipCapturedInput) { skipCapturedInput = false; return new(true); }
        if ((frame.Modifiers & 0x1000) == 0) GadgetSoundRequested?.Invoke("fontclick.wav");
        if ((flags & 0x100) == 0)
        {
            if (modelHit && !obstructed)
            {
                if ((flags & 1) != 0) CaretIndex = HitMultiline(point);
                return new(true,true);
            }
            IsFocused = false;
            return new(true,true,EnbUiCaptureChange.ReleaseLeft);
        }
        bool entered = Entered, focused = IsFocused;
        IsFocused = true; // Captured keys still dispatch after the release virtual changes row color.
        GadgetKeyResult = HandleKey(frame.Code,frame.Modifiers,translated);
        // The multiline placeholder flag is retired on acquisition, not by its raw text buffer.
        Entered = entered;
        bool escape = (GadgetKeyResult & EnbRetailEditorKeyResult.Escape) != 0;
        IsFocused = !escape && focused;
        // 00672680 changes row color even when00660250 has no mouse flags and
        // therefore does not release a capture slot (plain Escape has flags100).
        bool releaseLeft = escape && (captureFlags & 0x40) == 0 && (captureFlags & 0xf) != 0;
        return new(true,true,releaseLeft ? EnbUiCaptureChange.ReleaseLeft : EnbUiCaptureChange.None);
    }

    public void PrepareGadget(EnbUiPoint point, bool modelHit, bool obstructed, bool sliderCaptured, char translated = '\0')
    {
        gadgetPoint=point;gadgetHit=modelHit;gadgetObstructed=obstructed;gadgetCharacter=translated;
        Pointer.Prepare(QueryGadget(modelHit,sliderCaptured)?1u:0u);
    }

    //00673150/006732A0. The owner supplies the linked slider's current float value.
    public bool FocusGadget(bool force, ref uint flags, float sliderValue = 0)
    {
        if (!Multiline) throw new InvalidOperationException("Single-line activation belongs to the active-text owner.");
        var context=Pointer.CaptureContext;
        if (context.LeftCapture.Owner==Pointer && context.LeftCapture.Index==0) return false;
        if (!force && context.LeftCapture.Owner is not null) return false;
        if (force) { flags=1;context.CancelCaptures(flags); }
        else if ((flags&0xf)==0) flags=1;
        Pointer.AcquireCapture(0,flags);
        if (!force)
        {
            if (!Entered) Text="";
            Scroll=(int)sliderValue;
        }
        return true;
    }

    bool IEnbUiGadgetHandler.HasFlash(int index)=>false;
    bool IEnbUiGadgetHandler.AcceptsKeyboard(int index)=>true;
    bool IEnbUiGadgetHandler.TryHandle(int index,ref EnbUiPointerFrame frame,uint flags,out EnbUiGadgetResponse response)
    {
        response=HandleGadgetEvent(ref frame,flags,Pointer.LeftCapture==0,
            Pointer.CaptureContext.LeftCapture.Owner is null,gadgetHit,gadgetObstructed,gadgetPoint,gadgetCharacter);
        return true;
    }
    void IEnbUiGadgetHandler.CaptureAcquired(int index)=>IsFocused=true;
    void IEnbUiGadgetHandler.CaptureReleased(int index)=>IsFocused=false;
    void IEnbUiGadgetHandler.Retired(int index)=>ResetGadget();
    private void ResetGadget()
    {
        skipCapturedInput=false;GadgetKeyResult=EnbRetailEditorKeyResult.None;IsFocused=false;
        gadgetPoint=default;gadgetHit=gadgetObstructed=false;gadgetCharacter='\0';
    }
    public void RetireGadgetInput() { gadget?.Retire();ResetGadget(); }
}
