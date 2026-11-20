namespace EnbNext.Host.Sdl;

using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

// One native keyboard/text boundary for the actual world UI owners.
internal sealed class SdlWorldKeyboardTransport : IDisposable
{
    private readonly SdlWorldHost host;
    private readonly EnbRetailAssetBundle assets;
    private readonly Func<uint?> session;
    private readonly nint window;
    private readonly Action releasePointer,endCamera;
    private readonly Func<EnbFlightPointer?> pointer;
    private readonly List<(EnbUiTextInput Input,Func<bool>? Focus)> inputs=[];
    private const int PersistentInputs=4;
    private readonly HashSet<SDL_Scancode> held=[],tooltipAlt=[];
    private EnbRetailKeyBindings? bindings;
    private SdlFlightInput? translation;
    private EnbUiTextInput? resume;
    private bool focused=true,editing,disposed;
    private long flightTick=long.MinValue;
    private readonly SdlWorldFlightPointer? flightPointer;
    internal SdlStationInput Station {get;}
    internal EnbWorldKeyboard<SDL_Scancode> Owner {get;}
    internal EnbUiTextInput? Active {get {foreach(var entry in inputs)if(entry.Input is {IsVisible:true,IsEnabled:true,IsFocused:true} input)return input;return null;}}
    internal unsafe SdlWorldKeyboardTransport(SdlWorldHost host,EnbRetailAssetBundle assets,Func<uint?> session,
        Func<IEnbSceneFrame?> scene,SDL_Window* window,Action releasePointer,Func<EnbFlightPointer?> pointer,
        Action endCamera,Func<Task> screenshot,Action fullscreen,Action<int> stationCursor,Action restoreCursor,Action<string> report,SdlWorldFlightPointer? flightPointer=null)
    {
        this.flightPointer=flightPointer;this.host=host;this.assets=assets;this.session=session;this.window=(nint)window;
        this.releasePointer=releasePointer;this.pointer=pointer;this.endCamera=endCamera;
        Station=new(host,assets,session,window,stationCursor,restoreCursor,report);
        Owner=new(host.Owner,session,scene,host.FlightCommands,UpdateFlight,releasePointer,RequestWarp,screenshot,fullscreen);
        Attach(host.Owner.Chat.MainInput);Attach(host.Owner.Chat.VerifyInput);
        Attach(host.Owner.NavigationMap.NoteEditor.NameInput);Attach(host.Owner.NavigationMap.NoteEditor.NoteInput);
        host.Owner.Inventory.TextInputCreated+=Attach;host.Owner.Inventory.AssetsReleased+=ReleaseInventory;
        host.Owner.Options.FocusRequested+=RootFocus;host.Owner.FlightInputSuspended+=SuspendFlight;
        host.Owner.Chat.Defer=host.PostRefresh;
        host.Owner.Cockpit.ThrustPressed+=ThrustPressed;host.Owner.Cockpit.ThrustReleased+=ThrustReleased;
    }
    private void ThrustPressed(int direction)
    {host.FlightCommands.Input.Cockpit.Press(direction,assets.ResolvePlayerOptions(session()).Options.CpitThrottleClickLocks,Environment.TickCount64);UpdateFlight();}
    private void ThrustReleased(){host.FlightCommands.Input.Cockpit.Release();UpdateFlight();}
    private async void RequestWarp()=>await host.RequestWarpAsync();
    private void Attach(EnbUiTextInput input)
    {
        inputs.Add((input,input.FocusRequested));input.FocusRequested=()=>Focus(input);
        input.TransportChanged+=Sync;input.GotFocus+=FocusChanged;input.LostFocus+=FocusChanged;
    }
    private bool Focus(EnbUiTextInput input)
    {
        if(disposed||!input.IsVisible||!input.IsEnabled)return false;
        foreach(var entry in inputs)if(!ReferenceEquals(entry.Input,input))entry.Input.SetFocused(false);
        input.SetFocused(true);Sync();return true;
    }
    private void FocusChanged(object? sender,EventArgs e)=>Sync();
    private void RootFocus(){foreach(var entry in inputs)entry.Input.SetFocused(false);Sync();}
    internal unsafe void Sync()
    {
        if(!disposed&&focused)
        {
            long now=Environment.TickCount64;Station.Tick(now);
            if(flightTick==long.MinValue||now-flightTick>=16)
            {flightTick=now;if(!host.Owner.CanControlFlight)SuspendFlight();else UpdateFlight();}
        }
        bool next=!disposed&&focused&&(Active is not null||host.Owner.Options.InformationEditing);
        if(next==editing)return;NativePins.Check(next?SDL_StartTextInput((SDL_Window*)window):SDL_StopTextInput((SDL_Window*)window),"world text input");editing=next;
    }
    private void ReleaseInventory()
    {
        // Chat and map-marker inputs live as long as the world; inventory inputs follow its assets.
        for(int i=inputs.Count-1;i>=PersistentInputs;i--){Release(inputs[i]);inputs.RemoveAt(i);}resume=null;Sync();
    }
    private void Release((EnbUiTextInput Input,Func<bool>? Focus) entry)
    {
        entry.Input.TransportChanged-=Sync;entry.Input.GotFocus-=FocusChanged;entry.Input.LostFocus-=FocusChanged;
        entry.Input.FocusRequested=entry.Focus;entry.Input.SetFocused(false);
    }
    private string? ActionFor(SDL_KeyboardEvent key)
    {
        var current=assets.ResolveKeyBindings(session());
        if(!ReferenceEquals(bindings,current)){bindings=current;translation=new(current,_=>{},host.FlightCommands.Input){Modern=()=>host.Owner.Ui.ModernUi};}
        return translation!.ActionFor(key);
    }
    internal async void UpdateFlight()
    {
        var commands=host.FlightCommands;var world=host.Owner;
        if(commands.WarpSending)return;
        if(world.Cockpit.WarpEngaged){commands.Input.Suspend();releasePointer();world.Cockpit.SetThrust(0);return;}
        var intent=commands.Input.Resolve(world.CanControlFlight,world.Cockpit.CanThrust,Environment.TickCount64,world.Cockpit.IsThrustPointerInside,pointer());
        world.Cockpit.SetThrust(intent.Thrust);await commands.PumpAsync();
    }
    internal void BeginLayoutEditing(){RootFocus();SuspendGameplay();}
    internal void SuspendGameplay(){held.Clear();tooltipAlt.Clear();Owner.Keys.Clear();Station.Release();SuspendFlight();}
    private async void SuspendFlight()
    {
        endCamera();Owner.CancelShortcuts();host.FlightCommands.Input.Suspend();host.Owner.Cockpit.SetThrust(0);
        releasePointer();await host.FlightCommands.PumpAsync();
    }
    internal async Task<bool> HandleAsync(SDL_Event e)
    {
        if(disposed)return false;
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED)
        {
            focused=false;resume=Active;RootFocus();held.Clear();tooltipAlt.Clear();Owner.Keys.Clear();
            host.Owner.SetImmediateTooltips(false);host.Owner.NavigationMap.CancelPointer();host.Owner.GalaxyMap.CancelPointer();
            host.Owner.Cockpit.CancelPointerInput();host.Owner.Options.CancelPointer();host.Owner.Inventory.CancelPointer();
            Station.Release();SuspendFlight();return false;
        }
        if(type==SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED)
        {focused=true;if(resume is {} input)Focus(input);resume=null;Sync();return false;}
        if(!focused&&type!=SDL_EventType.SDL_EVENT_KEY_UP)return false;
        if(type==SDL_EventType.SDL_EVENT_TEXT_INPUT)
        {string text=Decode(e);bool used=host.Owner.Options.HandleText(text);if(!used&&Active is {} input){input.TextInput(text);used=true;}Sync();return used;}
        if(type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP && e.button.button==SDL_BUTTON_MIDDLE)
        {
            SDL_Event middle=default;middle.type=(uint)(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN?SDL_EventType.SDL_EVENT_KEY_DOWN:SDL_EventType.SDL_EVENT_KEY_UP);
            middle.key.mod=SDL_GetModState();middle.key.down=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN;
            return await HandleAsync(middle);
        }
        if(type is not (SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP))return flightPointer?.Handle(e)==true||Station.Handle(e);
        var key=new KeyEvent(this,e.key);Station.Modifiers(e.key.mod);
        if(type==SDL_EventType.SDL_EVENT_KEY_UP)
        {
            held.Remove(key.Key);Owner.Keys.Remove(key.Key);Owner.CameraKeys.Remove(key.Key);
            host.Owner.Options.HandleControlKeyUp(key.Code,key.QuitModifiers);
            if(host.Owner.Options.QuitOpen||host.Owner.Options.NativeOptionsOpen&&!host.Owner.Options.NativeControlsOpen&&key.Code==27)
                host.Owner.Options.QueuePanelKey(key.QuitCode,(ushort)(key.QuitModifiers|0x1000));
            if(key.IsAlt){tooltipAlt.Remove(key.Key);host.Owner.SetImmediateTooltips(tooltipAlt.Count!=0);}
            bool handled=host.FlightCommands.Input.KeyUp(key.Key);if(handled)UpdateFlight();
            bool station=Station.KeyUp(key.Key);
            return Owner.ShortcutKeyUp(key.Key,key.ShortcutKey,key.IsAlt)||handled||station;
        }
        // An OS repeat after SDL's focus-loss key-up is not a new press.
        if(e.key.repeat&&!held.Contains(key.Key))return true;
        held.Add(key.Key);
        if(key.IsAlt&&tooltipAlt.Add(key.Key))host.Owner.SetImmediateTooltips(true);
        if(host.Owner.Options.HandleControlKey(key.Code,key.QuitModifiers))return true;
        // A focused text box takes the key; it must not also walk the avatar.
        if(host.Owner.CanControlStation&&!host.Owner.TextEntryActive&&Station.KeyDown(key.Key,key.Action))return true;
        Active?.Key(key.ChatInput,()=>key.Handled=true);
        if(!key.Handled)await Owner.KeyDownAsync(key);
        Sync();return key.Handled;
    }
    private static unsafe string Decode(SDL_Event e)=>e.text.text==null?"":Marshal.PtrToStringUTF8((nint)e.text.text)??"";
    private sealed class KeyEvent(SdlWorldKeyboardTransport transport,SDL_KeyboardEvent key):IEnbWorldKeyEvent<SDL_Scancode>
    {
        public SDL_Scancode Key=>key.scancode;
        public int ShortcutKey=>(int)key.scancode;
        public ushort Code=>SdlOptionsKeyboard.Code(key);
        public ushort InventoryCode=>key.scancode==SDL_Scancode.SDL_SCANCODE_KP_ENTER?(ushort)13:Code;
        public ushort Modifiers=>(ushort)(QuitModifiers|(((uint)key.mod&SDL_KMOD_GUI)!=0?0x800:0));
        public ushort QuitCode=>Code is 0x10 or 0x11 or 0x12 or 0x1b or 0x51 or 0x43 or 0x4c?Code:(ushort)0x100;
        public ushort QuitModifiers=>SdlOptionsKeyboard.Modifiers(key.mod);
        public bool IsAlt=>key.scancode is SDL_Scancode.SDL_SCANCODE_LALT or SDL_Scancode.SDL_SCANCODE_RALT;
        public string? Action=>transport.ActionFor(key);
        public EnbUiInputEvent ChatInput=>new(Code,Modifiers,0,0,SdlOptionsKeyboard.Character(key));
        public bool Handled {get;set;}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        host.Owner.Inventory.TextInputCreated-=Attach;host.Owner.Inventory.AssetsReleased-=ReleaseInventory;
        host.Owner.Options.FocusRequested-=RootFocus;host.Owner.FlightInputSuspended-=SuspendFlight;
        host.Owner.Cockpit.ThrustPressed-=ThrustPressed;host.Owner.Cockpit.ThrustReleased-=ThrustReleased;
        Station.Dispose();SuspendFlight();Owner.Keys.Clear();
        foreach(var entry in inputs)Release(entry);inputs.Clear();resume=null;held.Clear();tooltipAlt.Clear();Sync();
    }
}
