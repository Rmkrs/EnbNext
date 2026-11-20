using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Engine;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    private EnbInformationUi? _informationUi;
    private readonly EnbCaptainLogController _captainLog = new();
    private bool _changingLogPage;
    private IEnbSceneFrame? _informationScene;
    public Action<string,bool,System.Numerics.Vector3>? AnimationSoundRequested {get;set;}
    private int _informationBlinkPeriod;
    private bool NativeInformationOpen => InformationOpen;
    private EnbInformationPanel? NativeInformationPanel => NativeInformationOpen ? _informationUi?.Find(_page==10?"log":_page==12?"complaint":"help") : null;
    private bool InformationOpen => _page is 10 or 11 or 12;
    public bool InformationEditing => InformationOpen && _informationUi?.Editing==true;
    private bool PetitionOpen => _page is 11 or 12 && _informationUi!.Petition.Category != 0;
    internal Func<uint, EnbRetailCaptainLog>? CaptainLogFactory { get; set; }
    public Func<uint, ulong, uint, string, string, string, Task<bool>>? SubmitPetition { get; set; }
    public Action<uint,string>? InformationMessage { get; set; }

    public bool OpenInformation(string name)
    {
        if (name == "Self Help") return OpenInformationBrowser(0, "Knowledgebase");
        if (!_interactive || _assets is null || _owner is not uint owner || owner == 0 || owner != session()) return false;
        try
        {
            if (name is not ("Get Help" or "Captain's Log")) return false;
            if (_captainLog.IsOpen && !SaveLog()) return false;
            CloseAll();
            _ = EnbRenderSession.Of(_assets).Information.Defaults;
            if (_informationBlinkPeriod == 0)
            {
                LoadQuitFontAttributes();
                var effects = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fontart.ini"));
                var blink = effects.EnumerateSections().Select(s=>s.Value).First(s=>s.GetValueOrDefault("EffectID")=="FA_BLINK");
                _informationBlinkPeriod = Math.Max(1,(int)(500/float.Parse(blink["Speed"],CultureInfo.InvariantCulture)));
            }
            LoadInformationAsset(name == "Get Help" ? "help" : "log");
            _informationUi!.Offset = default;
            if (name == "Captain's Log")
            {
                _informationUi!.Entry!.CharacterClass=EnbRetailEditorCharacterClass.Digits;
                _captainLog.Open(CaptainLogFactory?.Invoke(owner) ?? new(_assets.Paths.ClientOutputDirectory, owner),
                    owner, _informationUi!.Body!, _informationUi!.Entry!);
                var input=_informationUi.BindLog(_captainLog);
                input.SoundRequested-=InformationInputSound;input.SoundRequested+=InformationInputSound;
                input.SetScroll(0);_page = 10;
            }
            else _page = 11;
            _informationUi!.ResetEditors();
            if (_informationUi!.Body!.Text?.Length > 0 || _page == 10 && _captainLog.Initialized) _informationUi!.Body.Entered = true;
            Error = null; Opened?.Invoke(); Publish(); return true;
        }
        catch (Exception ex) when (InformationFailure(ex))
        { Error = "Could not open " + name + ": " + ex.Message; Publish(); return false; }
    }

    private void InformationInputSound(string sound)=>InteractionSoundRequested?.Invoke(sound);
    private static bool InformationFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    private void LoadInformationAsset(string key)
    {
        if(_informationUi is null)
        {
            _informationUi=new(EnbRenderSession.Of(_assets!).Information,_panelResources!,_quitPalette);
            _informationUi.CommandSoundRequested+=InformationInputSound;
            _informationUi.Petition.SendStarted+=Publish;
            _informationUi.Petition.Completed+=ApplyPetitionSend;
            _informationUi.AnimationSoundRequested+=(sound,flat,position)=>AnimationSoundRequested?.Invoke(sound,flat,position);
        }
        if (_informationUi.Find(key) is not null) return;
        _informationUi.Load(key);
    }

    private EnbUiRect InformationDesign(string key) => _informationUi!.Design(key);

    private bool SaveLog()
    {
        try { _captainLog.Save(_informationUi?.Body.Text ?? ""); return true; }
        catch (Exception ex) when (InformationFailure(ex))
        { Error = "Could not save Captain's Log: " + ex.Message; Publish(); return false; }
    }

    private void ChangeLogPage(int page)
    {
        if (_changingLogPage || !_captainLog.IsOpen || !SaveLog()) return;
        _changingLogPage = true;
        try
        {
            _captainLog.ReadPage(page, _informationUi!.Body!, _informationUi!.Entry!);
            _informationUi?.LogInput?.SetScroll(0);Error = null; Publish();
        }
        catch (Exception ex) when (InformationFailure(ex)) { Error = "Could not read Captain's Log: " + ex.Message; Publish(); }
        finally { _changingLogPage = false; }
    }

    /// <summary>Saves and closes the retail Captain's Log page if it is open (the Next Modern UI log then owns the pages).</summary>
    internal bool EndCaptainLog()
    {
        if (_page != 10) return true;
        if (!SaveLog()) return false;
        CloseAll(); return true;
    }

    private void EndInformation(bool preserveHelp=false)
    {
        if(_informationUi is null)return;
        _informationUi.EndPortraits(preserveHelp);
        if (_captainLog.IsOpen) SaveLog();
        _captainLog.Close();_informationUi.RetireForm();
    }

    private bool InformationVisible(string key)=>EnbInformationUi.Visible(key,_page,_informationUi?.Petition.Category??0);
    private void ActivateInformation(string key)
    {
        var command=_informationUi!.Activate(key,_page,_captainLog.Page);
        switch(command.Kind)
        {
            case EnbInformationCommandKind.AnswerPrompt:AnswerInformationPrompt(command.Value!=0);break;
            case EnbInformationCommandKind.CloseLog:if(SaveLog())CloseAll();break;
            case EnbInformationCommandKind.ChangeLogPage:ChangeLogPage(command.Value);break;
            case EnbInformationCommandKind.Focus:FocusRequested?.Invoke();Publish();break;
            case EnbInformationCommandKind.FollowBody:_informationFollowCaret=true;Publish();break;
            case EnbInformationCommandKind.Browser:OpenInformationBrowser(command.Value,command.Value==0?"Knowledgebase":command.Value==2?"Bug Submission":"Tech Support");break;
            case EnbInformationCommandKind.Complaint:
                try{LoadInformationAsset("complaint");_page=12;Publish();}
                catch(Exception ex) when(InformationFailure(ex)){Error=ex.Message;Publish();}
                break;
            case EnbInformationCommandKind.Petition:OpenPetition(unchecked((uint)command.Value));break;
            case EnbInformationCommandKind.Submit:_=SendPetitionAsync();break;
            case EnbInformationCommandKind.Import:ImportPetitionPlayer();break;
            case EnbInformationCommandKind.Leave:LeavePetition(command.Value!=0);break;
        }
    }

    private void PublishInformation(ImmutableList<EnbUiModelDraw>.Builder list)
    {
        Error=_informationUi!.Draw(list,_size,_scaling,_page,_informationUi!.Petition.Category,_captainLog.Page,_informationScene?.Environment,
            InformationBlink(),Error,ref _informationFollowCaret,Environment.TickCount64);
        if(_informationUi!.Prompt is not null)PublishControlDialog(list);
    }
    private void AdvanceInformationPortraits(double now)=>_informationUi?.AdvancePortraits(_page,now);
    private EnbUiRect InformationScreen(string key)=>_informationUi!.Screen(key,_size);

    private float InformationBlink()
    { float p=Math.Max(0,Environment.TickCount64)%_informationBlinkPeriod/(float)_informationBlinkPeriod;return p<=.5f?p*2:2-p*2; }

}
