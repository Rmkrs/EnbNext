using System.Diagnostics;
using EnbNext.Host.Sdl;

namespace EnbNext.App;

// Options > Graphics > Graphics card. Windows: presentation moves to another card while the game runs, like a
// resolution change. Every GL owner releases its objects (scene state and audio stay), the window's ANGLE device is
// created again on the chosen card, and the owners rebuild on the new context. Linux: the driver is settled when EGL
// loads, so the choice is saved and applied at the next start.
internal sealed partial class SdlApplication
{
    /// <summary>The card in use: a saved key, or null for the one the system picks for EnbNext.</summary>
    private string? activeGraphicsAdapter;

    /// <summary>The window on the saved card; the card the system picks when that card is missing or cannot start.</summary>
    private static SdlGameWindow CreateWindow(int width,int height,string? saved,out string? active)
    {
        active=null;
        if(OperatingSystem.IsLinux())
        {
            active=LinuxGraphicsAdapters.Apply(saved);
            return new SdlGameWindow("EnbNext",width,height,true);
        }
        if(OperatingSystem.IsWindows()&&WindowsGraphicsAdapters.Find(saved) is {} card)
        {
            try{var chosen=new SdlGameWindow("EnbNext",width,height,true,card.Luid);active=card.Key;return chosen;}
            catch(InvalidOperationException){}
        }
        return new SdlGameWindow("EnbNext",width,height,true);
    }

    private (string? Key,string Label)[] GraphicsAdapterChoices()
    {
        var adapters=window.CanSwitchAdapter?WindowsGraphicsAdapters.List():
            OperatingSystem.IsLinux()?[..LinuxGraphicsAdapters.List().Select(c=>c.Adapter)]:[];
        if(adapters.Length==0)return [];
        var result=new (string? Key,string Label)[adapters.Length+1];
        result[0]=(null,"Automatic");
        for(int i=0;i<adapters.Length;i++)result[i+1]=(adapters[i].Key,adapters[i].Name);
        return result;
    }
    /// <summary>The card shown as chosen: on Linux the saved one, which may wait for the next start.</summary>
    private string? ChosenGraphicsAdapter=>window.CanSwitchAdapter?activeGraphicsAdapter:_preferences.GraphicsAdapter;
    /// <summary>Linux: a card other than the one running takes effect at the next start.</summary>
    private bool GraphicsAdapterAtRestart(string? key)=>!window.CanSwitchAdapter&&key!=activeGraphicsAdapter;

    /// <summary>Runs the change between frames (after input, before the next frame is drawn), never during a screen change.</summary>
    private void PreviewGraphicsAdapter(EnbNext.Engine.EnbOptionsUi options,string? key)
    {
        if(!window.CanSwitchAdapter)return;
        Post(()=>{if(SwitchGraphicsAdapter(key) is {} reason)options.GraphicsAdapterFailed(reason);});
    }

    private void SaveGraphicsAdapter(string? key)
    {
        bool changed=_preferences.GraphicsAdapter!=key;
        _preferences=_preferences with {GraphicsAdapter=key};SavePreferences();
        if(changed&&GraphicsAdapterAtRestart(key)&&_client?.SelectedFixedSessionId is uint character)
        {
            string name=key is null?"Automatic":GraphicsAdapterChoices().FirstOrDefault(c=>c.Key==key).Label??key;
            _client.ReportLocalChatMessage(character,19,"Graphics card: "+name+" is used when EnbNext starts again.");
        }
    }

    private string? SwitchGraphicsAdapter(string? key)
    {
        if(key==activeGraphicsAdapter)return null;
        if(videoScreen is not null||movieScreen is not null)return "The graphics card can be changed when the video has ended.";
        long? luid=null;
        if(key is not null)
        {
            if(WindowsGraphicsAdapters.Find(key) is not {} card)return "That graphics card is not available.";
            luid=card.Luid;
            // Presentation already runs on this card (Automatic picked it): nothing moves.
            if(luid==window.AdapterLuid){activeGraphicsAdapter=key;return null;}
        }
        long started=Stopwatch.GetTimestamp();
        string? failure=null;
        lagMonitor.Hold(WorldSettle);
        surface.Deinitialize();
        worldScreen?.SuspendGpu();frontendScreen?.SuspendGpu();
        try{window.SwitchAdapter(luid);activeGraphicsAdapter=key;}
        catch(InvalidOperationException ex){failure="That graphics card could not be used: "+ex.Message;}
        frontendScreen?.ResumeGpu();worldScreen?.ResumeGpu();
        AppendDiagnostic(FormattableString.Invariant($"Graphics card changed in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms{(failure is null?"":" ("+failure+")")}. SDL presentation: {window.ContextDescription}"));
        return failure;
    }
}
