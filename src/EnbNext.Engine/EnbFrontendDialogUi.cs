using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect=EnbNext.Presentation.EnbUiRect;
using Point=EnbNext.Presentation.EnbUiPoint;
namespace EnbNext.Engine;
internal sealed class EnbFrontendDialogUi(EnbRetailAssetBundle assets,EnbFrontendTextUi text) : IDisposable
{
    private readonly EnbFrontendResources _resources=EnbRenderSession.Of(assets).Frontend;
    private readonly EnbFrontendTextUi _text=text;
    public event Action<string>? SoundRequested;
    public event Action<bool>? ActivityChanged;
    public event Action? QuitRequested;
    public void Dispose()=>Active?.Completion.TrySetResult(false);
    internal sealed record Dialog(string Message, TaskCompletionSource<bool> Completion, bool MessageOnly=false);
    public Dialog? Active {get;private set;}
    public Rect YesBounds,NoBounds;
    public void Key(EnbUiInputEvent input)
    {
        if(Active is not {} dialog)return;
        // 587D50 forces modifier 0x400 for option letters; 4307A0 establishes
        // that as Alt (not ordinary character input). 429A20 matches both words.
        if(input.Modifiers==0x400)
        {
            if(_resources.DialogDefinitions.TryGetSection(dialog.MessageOnly?"OK":"YES_NO",out var definition))
                for(int i=0;i<(dialog.MessageOnly?1:2);i++)
                    if(EnbRetailKeyIdentity.Code(definition.GetValueOrDefault("Key"+i,"")) is var authored && authored!=0 && input.Code==authored)
                    {CompleteDialog(i==0);return;}
        }
        // 00588480 registers only the authored option records; 00588A20
        // consumes their nonzero actions. OK / YES_NO define no implicit
        // Escape or Enter action (00587D50, installed dialogbox.ini).
    }
    public Task ShowMessageAsync(string message)
    {
        if(Active is not null)throw new InvalidOperationException("A dialog is already open.");
        Active=new(message,new(TaskCreationOptions.RunContinuationsAsynchronously),true);
        ActivityChanged?.Invoke(true);
        SoundRequested?.Invoke("Roll_Select02b.wav"); // 588A70
        return Active.Completion.Task;
    }
    public Task<bool> ConfirmAsync(string message)
    {
        if(Active is not null)throw new InvalidOperationException("A dialog is already open.");
        Active=new(message,new(TaskCreationOptions.RunContinuationsAsynchronously));
        ActivityChanged?.Invoke(false);
        SoundRequested?.Invoke("Roll_Select02b.wav"); // 588A70
        return Active.Completion.Task;
    }
    public async Task ConfirmQuitAsync()
    {
        if (Active is not null) return;
        if (await ConfirmAsync("Are you sure you want to quit?")) QuitRequested?.Invoke();
    }
    public void HandleEscape()
    {
        if (Active is not null) return;
        SoundRequested?.Invoke("Login_Mouse_Click");
        _ = ConfirmQuitAsync();
    }
    public void CompleteDialog(bool accepted)
    {
        if (Active is not {} dialog) return;
        SoundRequested?.Invoke("Roll_Select02b.wav"); // 588A20
        Active=null;
        ActivityChanged?.Invoke(false);
        dialog.Completion.TrySetResult(accepted);
    }
    public void Click(Point position)
    {
        if(Active is not {} dialog || (!YesBounds.Contains(position)&&!NoBounds.Contains(position)))return;
        CompleteDialog(YesBounds.Contains(position));
    }
    public void Render(ImmutableArray<EnbFrontendDraw>.Builder draws,EnbResolvedRenderCamera camera,Rect bounds,Point pointer)
    {
        if(Active is not {} dialog)return;
        Rect Screen(Rect r)=>new(r.X*bounds.Width/640,r.Y*bounds.Height/480,r.Width*bounds.Width/640,r.Height*bounds.Height/480);
        if(!_resources.DialogDefinitions.TryGetSection(dialog.MessageOnly?"OK":"YES_NO",out var definition))return;
        bool small=definition.GetValueOrDefault("FontSize")=="FONT_SMALL";
        if(!_resources.DialogLabels.TryResolve(small?"DIALOG_INFO_SMALL":"DIALOG_INFO",out var body) ||
            !_resources.DialogLabels.TryResolve("OPTION_INFO_0",out var option))return;
        string bodyFont=body.Properties["FontType"],optionFont=option.Properties["FontType"];
        var font=_text.Font(bodyFont,bounds.Size);var buttonFont=_text.Font(optionFont,bounds.Size);
        if(font is null || buttonFont is null)return;
        float fx=font.Scale*640/(float)bounds.Width,fy=font.Scale*480/(float)bounds.Height;
        float ox=buttonFont.Scale*640/(float)bounds.Width,oy=buttonFont.Scale*480/(float)bounds.Height;
        // 67B270 -> 588A70 -> 588DC0 uses the default wrapper, width 320;
        // 589740 selects the authored font and retained row count.
        string[] lines=EnbRetailDialogText.Wrap(dialog.Message,320,c=>font.CharacterAdvance(c)*fx).Take(small?16:10).ToArray();
        string[] options=Enumerable.Range(0,dialog.MessageOnly?1:2).Select(i=>definition["Option"+i]).ToArray();
        EnbDialogBorder Border(string name)
        {
            var d=_resources.DialogFrame(name).Definition;
            return new((float)d.BorderX,(float)d.BorderY);
        }
        var layout=EnbRetailDialogLayout.Measure(lines.Select(line=>font.MeasureWidth(line)*fx).ToArray(),font.CellHeight*fy,
            options.Select(text=>buttonFont.MeasureWidth(text)*ox).ToArray(),buttonFont.CellHeight*oy,
            Border("DIALOG_BOX_WINDOW"),Border("DIALOG_BOX_INFO"),Border("DIALOG_BOX_OPTION"));
        var placed=EnbRetailDialogLayout.Place(layout.Window.Width,layout.Window.Height,320,240);
        Rect Place(EnbDialogRect r)=>new(placed.X+r.X,placed.Y+r.Y,r.Width,r.Height);
        var box=Place(layout.Window);
        var dialogScreenBounds=Screen(box);
        foreach(var (id,label) in _text.Labels)
        {
            if(id.StartsWith("dialog",StringComparison.Ordinal) || !label.IsVisible)continue;
            var labelBounds=new Rect(label.X,label.Y,label.Width,label.Height);
            if(labelBounds.Intersects(dialogScreenBounds))label.IsVisible=false;
        }
        Frame("DIALOG_BOX_WINDOW",box);
        Frame("DIALOG_BOX_INFO",Place(layout.Info));
        YesBounds=Screen(Place(layout.Options[0]));
        NoBounds=dialog.MessageOnly?default:Screen(Place(layout.Options[1]));
        for(int i=0;i<lines.Length;i++)
            _text.PlaceCentered("dialogMessage"+i,lines[i],Place(layout.Lines[i]),bodyFont,_resources.Color(body.Properties["Color"]),bounds.Size);
        for(int i=0;i<options.Length;i++)
        {
            Frame("DIALOG_BOX_OPTION",Place(layout.Options[i]));
            _text.PlaceCentered(i==0?"dialogYes":"dialogNo",options[i],Place(layout.Labels[i]),optionFont,_resources.Color(option.Properties["Color"]),bounds.Size);
        }
        void Frame(string name,Rect rect)
        {
            var asset=_resources.DialogFrame(name);
            var geometry=asset.Geometry((float)rect.Width,(float)rect.Height);
            var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
            var world=Matrix4x4.CreateTranslation(-min.X,-max.Y,0)*Matrix4x4.CreateTranslation((float)rect.X-320,240-(float)rect.Y,100);
            draws.Add(new("dialog/"+name+rect.X,asset.Resource,asset.Resource.Meshes.Single(),world,geometry,camera,0)
                { Highlight = name == "DIALOG_BOX_OPTION" && Screen(rect).Contains(pointer) });
        }
    }
}
