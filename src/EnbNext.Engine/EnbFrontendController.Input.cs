using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbFrontendController
{
    public bool Key(EnbUiInputEvent input,bool repeat=false)
    {
        if(_screen=="login"&&input.Code is 0x0d or 0x100)
        {if(repeat)return true;input=input with {Code=0x0d};}
        bool handled=false;
            if(Owner.Dialogs.Active is not null) { Owner.Dialogs.Key(input);handled=true;return true; }
            if(Owner.Groups.Key(input))return true;
            if(Owner.Accounts.IsOpen)return Owner.Accounts.Key(input);
            if(input.Code==0x1b)
            {
                HandleEscape();
                handled=true;
            }
            else if(input.Code==0x0d)
            {
                if(_busy || SelectionMoving)return false;
                if(_screen=="login")RequestLogin();
                else if(_screen=="characters" && _selected>=0)
                { EnterRequested?.Invoke(); }
                else if(_screen=="creation" && _creationPage==CreationPage.Name)NextCreationPage();
                else if(_screen=="creation" && _creationPage==CreationPage.Ship && _shipTab==3)
                {
                    _creationTextEditing=false;
                    _creationShipName.IsEnabled=false;
                }
                handled=true;
            }
        return handled;
    }
    private void RequestLogin()
    {if(!_busy&&!Owner.Accounts.Busy)LoginRequested?.Invoke();}
    public void PointerMove(EnbUiPoint position)
    {
        if(_creationSkillDragging)SetCreationSkillScroll(position);
        else if(_creationBodyDragging)SetCreationBodyFromPointer(position);
        else if(_creationPaletteDragging)CreationClick("COLOR_PALETTE",position);
        else if(_creationDragging)DragCreation(position);
        else UpdateHover(position);
    }
    public bool Wheel(double delta)
    {
        if(Owner.Accounts.Wheel(delta))return true;
        if(_screen!="creation" || _creationPage!=CreationPage.Profession || _busy || Owner.Dialogs.Active is not null)return false;
        ScrollCreationSkills(delta>0?-1:1);return true;
    }
    public void PointerExit(){_hover="";_hoverBeganTick=0;_pointerInside=false;}
    public void ReleasePointer(){_pressedName="";EndCreationControlHold();_creationDragging=false;_creationSkillDragging=false;_creationBodyDragging=false;_creationPaletteDragging=false;}
    public (bool Handled,bool Capture) PointerPress(EnbUiPoint position)
    {
        bool handled=false,capture=false;
            if(Owner.Dialogs.Active is not null){Owner.Dialogs.Click(position);handled=true;return (handled,capture);}
            if (_busy || SelectionMoving || _screen=="creation" && CreationNameMoving) return (handled,capture);
            if(_screen=="login" && Owner.Groups.Click(position))return (true,false);
            if(_screen=="login" && !Owner.Groups.GroupMode && Owner.Accounts.Click(position)){handled=true;return(handled,capture);}
            if(HandleServerClick(position)){handled=true;return (handled,capture);}
            var id = Hit(position);
            if(_screen=="login" && Owner.Groups.GroupMode && id is "BUTTON_NAME" or "BUTTON_PASSWORD")return (true,false);
            if (_screen == "creation")
            {
                if((_creationPage==CreationPage.Ship && _shipTab==3 || _creationPage==CreationPage.Name) &&
                    _hits.TryGetValue("NAME",out var nameField) && nameField.Contains(position))
                    id="NAME";
                if(id is not null)
                {
                    _pressedName=id;
                    if (CreationClickHasSound(id))
                        _gpu.QueueFrontendSound(_creationPage==CreationPage.Ship &&
                            id is not ("CONFIRM" or "CANCEL" or "BUTTON_CANCEL" or "EXIT" or "NAME")
                            ? "ButtonClick02.wav" : "Login_Mouse_Click");
                    if(TryBeginCreationSkillDrag(id)){capture=true;handled=true;return (handled,capture);}
                    if(TryBeginCreationBodyDrag(id,position)){capture=true;handled=true;return (handled,capture);}
                    if(id=="COLOR_PALETTE" && _creationPage is CreationPage.Avatar or CreationPage.Ship)
                    {
                        _creationPaletteDragging=true;
                        CreationClick(id,position);
                        capture=true;
                        handled=true;
                        return (handled,capture);
                    }
                    CreationClick(id,position);handled=true;
                    // The ship-page hover callback at client.exe 00628900
                    // includes Random (widget 0xB). Retail replays that hover
                    // cue on its click/refresh, in addition to 00623080's
                    // short ButtonClick02 cue. The retained _hover id otherwise
                    // suppresses that second event in this frontend.
                    if(_creationPage==CreationPage.Ship && id=="RANDOM")
                        _gpu.QueueFrontendSound("Login_Mouse_Over_Buttons");
                    BeginCreationControlHold(id);
                    if(IsCreationContinuousControl(id) || id=="PAN")capture=true;
                }
                else if(TryBeginCreationDrag(position)){capture=true;handled=true;}
                return (handled,capture);
            }
            if(id is null || (id is "CONTINUE" or "DELETE" && _selected < 0))return (handled,capture);
            if (_hits.TryGetValue(id, out var buttonBounds) && buttonBounds.Contains(position)
                && id.StartsWith("NAME", StringComparison.Ordinal))
            { _pressedName = id; capture=true; }
            // MainWindow starts the entry cue here before handing audio to the world.
            // The login button gives the same click feedback as the other login buttons.
            if(id is not "CONTINUE") _gpu.QueueFrontendSound(_screen == "login" || id == "CANCEL" ? "Login_Mouse_Click" : id == "DELETE" ? "Avatar_Delete_Character" : "ButtonClick02.wav");
            switch (id)
            {
                case "BUTTON_NAME": Username.Text=""; Username.Focus(); handled=true; break;
                case "BUTTON_PASSWORD": Password.Text=""; Password.Focus(); handled=true; break;
                case "BUTTON_NEW": RequestLogin(); break;
                case "BUTTON_REGISTRA": AccountRequested?.Invoke(); break;
                case "BUTTON_IMPORT": if(!Owner.Accounts.Busy)Owner.Accounts.OpenImport(); break;
                case "EXIT": _ = ConfirmQuitAsync(); break;
                case "CONTINUE": if (_selected >= 0) EnterRequested?.Invoke(); break;
                case "CANCEL": PreviousCharacterView(); break;
                case "DELETE": if (_selected >= 0 && !_characters[_selected].IsEmpty)
                    { BeginCharacterDeleteGesture(); DeleteRequested?.Invoke(); } break;
                default:
                    if (id?.StartsWith("NAME") == true && int.TryParse(id.AsSpan(4), out int slot) && slot < _characters.Count)
                    { SelectCharacter(slot); }
                    break;
            }
        return (handled,capture);
    }
}
