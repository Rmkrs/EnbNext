using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;
using EnbNext.Engine;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    private bool _informationFollowCaret = true;
    private static readonly EnbUiRaster InformationCaret = EnbRetailTextEditor.CaretRaster;

    private void CommitLogPage()
    {
        string value = _informationUi?.Entry.Text ?? "";
        if (!int.TryParse(value, out int page)) page = 1;
        if (page != _captainLog.Page) ChangeLogPage(page);
        else _informationUi!.Entry!.Text = _captainLog.Page.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OpenPetition(uint category)
    {
        if (PetitionOpen) return;
        try
        {
            LoadInformationAsset("petition");
            if (category >= 3) LoadInformationAsset("player");
            var input=_informationUi!.BeginPetition(category,_confirmationPanel!,_confirmation);
            input.SoundRequested-=InformationInputSound;input.SoundRequested+=InformationInputSound;
            Error = null; Publish();
        }
        catch (Exception ex) when (InformationFailure(ex)) { Error=ex.Message; Publish(); }
    }

    private void InformationPrompt(string section,bool back=false)
    {if(_informationUi!.ShowPrompt(section,back,_confirmation) is {} error)Error=error;Publish();}

    private void LeavePetition(bool back)
    {
        if (_page==10) { if(SaveLog()) CloseAll(); return; }
        if (_informationUi!.Petition.Sending || _informationUi!.Prompt is not null) return;
        // 005D4F60/005D4DE0: ask only when the form passes its full validation (OPT-D13).
        if (PetitionOpen && _informationUi!.ValidationPrompt(_page==12) is null) { InformationPrompt("PETITION_ENTRY_CANCEL",back); return; }
        FinishPetition(back);
    }

    private void FinishPetition(bool back)
    {
        _informationUi!.FinishPetition(back,_confirmation);
        if(!back){CloseAll();return;}
        Publish();
    }

    private void AnswerInformationPrompt(bool yes)
    {
        var answer=_informationUi!.AnswerPrompt(yes,_confirmation);
        if(answer.Canceled)
        {
            if (_owner is uint owner) InformationMessage?.Invoke(owner,"Petition canceled.");
            FinishPetition(answer.Back);
        }
        else Publish();
    }

    private Task SendPetitionAsync()
    {
        if (_informationUi!.Petition.Sending || !PetitionOpen || _owner is not uint owner || owner != session()) return Task.CompletedTask;
        if(_informationUi!.ValidationPrompt(_page==12) is {} prompt){InformationPrompt(prompt);return Task.CompletedTask;}
        string player=_page==12?_informationUi.Player.Text:"";
        return _informationUi!.Petition.SendAsync(owner, _handoff, _informationUi!.Entry!.Text!, _informationUi!.Body!.Text!, player, session, SubmitPetition);
    }

    private void ApplyPetitionSend(EnbPetitionSendResult result)
    {
        // Applied synchronously with the engine's retirement check: no second await can cross character lifetimes.
        if (result == EnbPetitionSendResult.Sent) FinishPetition(false);
        else if (result == EnbPetitionSendResult.Failed) { Error = _informationUi!.Petition.Error; Publish(); }
    }

    private void ImportPetitionPlayer()
    {
        if(!_informationUi!.ImportPlayer(_informationScene))
        {if(_owner is uint owner)InformationMessage?.Invoke(owner,"Player target must be selected.");InteractionSoundRequested?.Invoke("failure_1.wav");Publish();return;}
        InteractionSoundRequested?.Invoke("fontclick.wav");
        Publish();
    }

}
