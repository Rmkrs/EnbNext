using System.Globalization;
namespace EnbNext.Engine;

internal enum EnbInformationCommandKind { None,AnswerPrompt,CloseLog,ChangeLogPage,Focus,FollowBody,Browser,Complaint,Petition,Submit,Import,Leave }
internal readonly record struct EnbInformationCommand(EnbInformationCommandKind Kind,int Value=0);
internal sealed partial class EnbInformationUi
{
    public event Action<string>? CommandSoundRequested;
    public static bool Visible(string key,int page,uint category)
    {
        if(key.StartsWith("log/"))return page==10;
        if(key.StartsWith("help/"))return page==11;
        if(key.StartsWith("complaint/"))return page==12;
        if(key.StartsWith("petition/"))return page is 11 or 12 && category!=0;
        if(key.StartsWith("player/"))return page==12 && category!=0;
        return false;
    }
    public EnbInformationCommand Activate(string key,int page,int logPage,DateTime? date=null)
    {
        if(Prompt is not null)return key is "control/dialog/yes" or "control/dialog/no"?new(EnbInformationCommandKind.AnswerPrompt,key.EndsWith("/yes")?1:0):default;
        if(!Visible(key,page,Petition.Category)||!EnbInformationPanel.Enabled(key,page,Petition.Category,logPage)||Petition.Sending)return default;
        if(page is 11 or 12 && Petition.Category!=0 && (key.StartsWith("help/")||key.StartsWith("complaint/"))&&!key.EndsWith("/EXIT2"))return default;
        if(key is "log/EXIT2" or "log/PREVIOUS" or "log/NEXT" or "log/DATE_STAMP" or "petition/EXIT_BUTTON" or "petition/SUBMIT" || key.StartsWith("help/")&&EnbInformationPanel.IsButton(key))
            CommandSoundRequested?.Invoke("Roll_Select02b.wav");
        if(key=="log/EXIT2")return new(EnbInformationCommandKind.CloseLog);
        if(page==10 && key is "log/PREVIOUS" or "log/NEXT" or "log/DATE_STAMP" && Entry.IsFocused){Entry.IsFocused=false;return default;}
        if(key=="log/PREVIOUS")return new(EnbInformationCommandKind.ChangeLogPage,logPage-1);
        if(key=="log/NEXT")return new(EnbInformationCommandKind.ChangeLogPage,logPage+1);
        if(key=="log/DATE_STAMP")
        {
            // 005ABB10 -> A29BCC, retained CRT C-locale month table BCF9E0.
            Body.Insert((date??DateTime.Now).ToString("MMMM dd, yyyy: ",CultureInfo.InvariantCulture));
            LogInput!.RequestBodyFocus();return new(EnbInformationCommandKind.FollowBody);
        }
        if(key.EndsWith("/MESSAGE",StringComparison.Ordinal)){FocusEditor(Body);return new(EnbInformationCommandKind.Focus);}
        if(key=="log/ENTRY_FIELD00"){LogInput!.RequestPageEdit();return new(EnbInformationCommandKind.Focus);}
        if(key.EndsWith("/ENTRY_FIELD00",StringComparison.Ordinal)){FocusEditor(Entry);return new(EnbInformationCommandKind.Focus);}
        if(key=="player/ENTRY_FIELD01"){FocusEditor(Player);return new(EnbInformationCommandKind.Focus);}
        if(key=="help/KNOWLEDGE")return new(EnbInformationCommandKind.Browser,0);
        if(key=="help/BUG")return new(EnbInformationCommandKind.Browser,2);
        if(key=="help/BILLING")return new(EnbInformationCommandKind.Browser,1);
        if(key=="help/PLAYER")return new(EnbInformationCommandKind.Complaint);
        if(key=="help/GAMEPLAY")return new(EnbInformationCommandKind.Petition,1);
        if(key=="help/STUCK")return new(EnbInformationCommandKind.Petition,2);
        if(key.StartsWith("complaint/BUTTON_",StringComparison.Ordinal)&&uint.TryParse(key.AsSpan(17),out uint category))return new(EnbInformationCommandKind.Petition,unchecked((int)(category+2)));
        if(key=="petition/SUBMIT")return new(EnbInformationCommandKind.Submit);
        if(key=="player/EXIT_IMPORT")return new(EnbInformationCommandKind.Import);
        if(key is "help/EXIT2" or "complaint/EXIT2" or "petition/EXIT_BUTTON" or "petition/BACK")return new(EnbInformationCommandKind.Leave,key=="petition/BACK"?1:0);
        return default;
    }
}
