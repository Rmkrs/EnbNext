using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    public Func<EnbStationTerminalRequest, Task<bool>>? TerminalRequested { get; set; }
    public Func<EnbStationTerminalOwner, string?>? StationBrowserQuery { get; set; }
    private IEnbInventoryBrowser? _stationBrowser;
    public bool BrowserOpen => _stationBrowser is not null;
    public Func<bool>? OtherBrowserOpen { get; set; }
    private EnbStationTerminalOwner? _terminal;
    public EnbStationTerminalOwner? TerminalOwner => _terminal;
    private EnbStationTerminalOwner? _terminalEntering;
    public EnbStationTerminalOwner? TerminalCameraOwner => _terminalEntering ?? _terminal;
    public Func<EnbStationTerminalOwner, Task<bool>>? TerminalCameraRequested { get; set; }
    private long _terminalGeneration;
    private long _terminalOpenedAt;
    private string _terminalArt = "";
    private int _terminalPage;
    private int _terminalSelectedFormula=-1, _terminalRememberedFormula=-1;
    private (bool Recent,int Depth,uint Lab,ulong Revision)? _terminalFormulaSource;
    private readonly Dictionary<string,EnbAttachedTextStyle> _terminalListStyles = [];
    private readonly Dictionary<string,EnbAttachedLabelScroll> _terminalListScroll = [];
    private readonly Dictionary<string,EnbAttachedLabelHover> _terminalListHover = [];
    private (int Page,bool Recent,int Depth,ulong Revision)? _terminalListRefresh;
    private bool _terminalRecent;
    private bool _terminalInitialMenuClosed;
    private bool _terminalTickPending;
    private long _terminalLastTick;
    private ulong _terminalResultSeen;
    private ulong _terminalPanelOpeningRevision;
    private readonly Dictionary<string, EnbInterfaceTextDefinition[]> _terminalLabels = [];
    private readonly Dictionary<string, EnbAuthoredUiAsset> _terminalAssets = [];
    private readonly Dictionary<string, EnbW3dRenderHierarchyAnimation> _terminalAnimations = [];
    private readonly Dictionary<string,bool> _terminalPose = [];
    private readonly Dictionary<string,(double Frame,long Tick)> _terminalFrames = [];
    private EnbRetailIniDocument? _terminalTooltips;
    private readonly Dictionary<string,EnbRgb> _terminalColors = [];
    private readonly Dictionary<string, (EnbTerminalCommand Command, uint Value)> _terminalActions = [];
    private EnbStationTerminalSnapshot? Terminal => _scene?.StationTerminal;
    // Retail 005B16C0 consumes Exit/Escape while the manufacturing
    // presentation countdown at +BA8 is nonzero (005B3A70/005B1230).
    private bool ManufacturingExitBlocked => _terminal?.Type == 2 && Terminal?.ActionCountdownMilliseconds > 0;
    private bool JobsRequestPending => _terminal?.Type == 3 && _scene?.StationJobRequestPending == true;
    // 005458B0 sets +110 until result presentation clears it in005443F0.
    // 00544BB0 returns AL=0 for Escape while that analysis is outstanding.
    private bool AnalysisExitBlocked => _terminal?.Type == 1 && Terminal?.ActionContext == Net7ManufacturingActionContext.Analyze;

    public async Task OpenStationTerminal(uint station, ulong version, uint room, EnbSceneStationFacility facility)
    {
        if (!_interactive || _busy || _terminalEntering is not null || session() is not uint character || TerminalRequested is null || facility.Type > 6) return;
        if (facility.Type == 4 && OtherBrowserOpen?.Invoke() == true) return;
        // 0063B630 starts the camera; 006390A0 waits for view+E4 to clear
        // before 0069F8F0 constructs the interface and its requests.
        var selected = new EnbStationTerminalOwner(character, station, version, room, facility.Slot, facility.Type);
        // Next Modern UI: a compact window, without the camera move (the avatar still turns and takes the terminal pose).
        _terminalCompact = CompactTerminal(facility.Type);
        if (TerminalCameraRequested is {} transition)
        {
            CloseAll();
            long generation = _terminalGeneration;
            _terminalEntering = selected;
            bool completed;
            try { completed = await transition(selected); }
            finally { if (_terminalEntering == selected && generation == _terminalGeneration) _terminalEntering = null; }
            if (!completed || generation != _terminalGeneration || session() != character) return;
        }
        string model = facility.Type switch { 0 => "tsm03a", 1 => "tsm02a", 2 => "tsm01a", 3 => "jnews00", 5 => "buy_ship01", 6 => "buy_avatar00", _ => "jweb00" };
        try { LoadTerminal(model); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Error = ex.Message; return; }
        CloseAll(); _terminalArt = model; _terminalPage = 0; _terminalRecent = false;
        _terminalPanelOpeningRevision=Terminal?.ComponentPanelRevision??0;
        // 0069CDE0 releases the terminal controller on close; a new opening
        // owns a fresh mesh request, even though we cache the immutable assets.
        _terminalFrames.Remove(model); _terminalPose.Remove(model);
        SetTerminalPose(model,false);
        _terminalInitialMenuClosed = true;
        _terminal = selected;
        ResetCustomization();
        _terminalOpenedAt=Environment.TickCount64;
        _terminalResultSeen = Terminal?.Result?.Revision ?? 0;
        ResetManufacturingLabels();
        // A compact window opens the Modern inventory beside it instead of the retail cargo (and closes it again).
        _cargo = facility.Type < 3 && !_terminalCompact;
        if (_terminalCompact && facility.Type < 3) { _terminalOpenedModernInventory = !_modernInventory; _modernInventory = true; RaiseModern("minv"); }
        _jobSelected = null; _jobsSeen.Clear(); _jobType = 0; _jobSortColumn=0; _jobSecondarySort=1; _jobTertiarySort=2; _jobSortDescending=false; _jobReward=""; Opened?.Invoke();
        var openingOwner = _terminal;
        await SendTerminal(() => TerminalRequested(new(openingOwner, EnbTerminalCommand.Open)));
        if (_terminal != openingOwner) return;
        if (_message is not null) { _terminal = null; _cargo = false; if (_terminalOpenedModernInventory) { _terminalOpenedModernInventory = false; CloseModernInventory(); } }
        else if (facility.Type == 4) OpenStationBrowser(openingOwner);
        Publish();
    }

    private void OpenStationBrowser(EnbStationTerminalOwner owner)
    {
        try
        {
            string query = StationBrowserQuery?.Invoke(owner) ?? throw new InvalidOperationException("Station session changed.");
            var address = EnbRetailNewsAddress.Load(_assets!.Paths.ClientExecutablePath);
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EnbNext", "Browser", "Station", owner.Character.ToString("X8"));
            var browser = (BrowserFactory??throw new InvalidOperationException("Browser transport unavailable"))(EnbNext.Net7.Net7StationBrowser.AppendQuery(address, query),profile,query);
            _stationBrowser = browser;
            browser.Failed += _ => { if (_stationBrowser == browser) { browser.IsVisible = false; _message = "The news page could not be opened. Close this terminal and reopen it to try again."; Publish(); } };
            browser.PageLoaded += () => { if (_stationBrowser == browser) { _message = null; browser.IsVisible = true; Publish(); } };
            browser.CloseRequested += () => { if (_stationBrowser == browser) { ClearTerminal(); Publish(); } };

        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or BadImageFormatException)
        { _message = "The news page could not be opened. Close this terminal and reopen it to try again."; }
    }
    private void LoadTerminal(string model)
    {
        if (_terminalLabels.ContainsKey(model)) return;
        // The retail computer backdrop uses the same authored six texture
        // panels preserved in JWEB00.BOX01 (also visible in the retail receipts).
        if(model!="jweb00")LoadTerminal("jweb00");
        var a = _assets!;
        if(_terminalColors.Count==0)
            foreach(var section in EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory,"fontart.ini")).EnumerateSections().Select(s=>s.Value))
                if(section.GetValueOrDefault("ColorID") is {} colorId && section.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
                    _terminalColors[colorId]=new(byte.Parse(rgb[0])/255f,byte.Parse(rgb[1])/255f,byte.Parse(rgb[2])/255f);
        _terminalTooltips ??= EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory,"tooltip.ini"));
        var nameResources=model=="buy_ship04"?EnbRenderSession.Of(a).ShipName:null;
        var asset = nameResources?.Asset ?? EnbAuthoredUiAsset.Load(a, model + ".w3d") ?? throw new InvalidDataException(model);
        _terminalAssets[model]=asset;
        if(model is "tsm01a" or "tsm03a")
        {
            if(!a.RetailMixResources!.TryReadResource(model+"-1.w3d",out var bytes,out _) || !EnbW3dModel.TryDecode(bytes,out var animationModel) ||
                animationModel.Animations.FirstOrDefault() is not {} animation || !EnbW3dRenderResourceCompiler.TryCompileAnimation(0,animation,out var compiled,out _))
                throw new InvalidDataException("Missing terminal animation: "+model);
            _terminalAnimations[model]=compiled;
        }
        string file = Path.Combine(a.Paths.ClientIniDirectory, "fa_" + model + ".ini");
        var ini = EnbRetailIniDocument.Load(file); var layout = nameResources?.Layout ?? EnbInterfaceLayoutCatalog.Load(file);
        _terminalLabels[model] = ini.EnumerateSections().Select(s => s.Value.GetValueOrDefault("StringID"))
            .Where(id => id is not null).Select(id => layout.TryResolve(id!, out var d) ? d : null).OfType<EnbInterfaceTextDefinition>().ToArray();
        SetTerminalPose(model,false);
        if(model is "tsm01a" or "tsm03a" or "jnews00"){_terminalFrames.Remove(model);}
        if(model=="jnews00")LoadCharacterText(a,asset,"jobs");
        if(model is "tsm01a" or "tsm03a" or "jnews00")LoadTerminalSlider();
    }
    private void SetTerminalPose(string model,bool expanded)
        => UpdateTerminalPose(model,expanded,Environment.TickCount64);

    private void UpdateTerminalPose(string model,bool expanded,long now)
    {
        var asset=_terminalAssets[model];
        expanded |= _terminalPose.GetValueOrDefault(model);
        var animation=expanded?_terminalAnimations.GetValueOrDefault(model):null;
        bool existed=_terminalFrames.TryGetValue(model,out var previous);
        double target=expanded && animation is not null?animation.NumFrames-1:0;
        double frame=existed?previous.Frame:0;
        double step=animation is null?0:Math.Max(0,now-previous.Tick)*animation.FrameRate/1000d;
        bool changed=_terminalPose.GetValueOrDefault(model)!=expanded;
        _terminalPose[model]=expanded;
        if(existed)frame=frame<target?Math.Min(target,frame+step):Math.Max(target,frame-step);
        _terminalFrames[model]=(frame,now);
        if(frame!=target)_animatingText=true;
        if(existed && !changed && previous.Frame==frame)return;
        if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),Matrix4x4.Identity,animation,
            (float)frame,out var pose,out var error))throw new InvalidDataException(error);
        foreach(var m in asset.Resource.Meshes)
        {
            EnbW3dDeformedGeometry geometry;
            if(m.BoneIndices.Count>0)
            {if(!EnbW3dSkinDeformer.TryDeform(m,pose,out geometry,out error))throw new InvalidDataException(error);}
            else
            {
                var p=asset.Hierarchy.Pivots.First(x=>x.Name.Equals(m.Name,StringComparison.OrdinalIgnoreCase));
                var transform=pose.WorldTransforms[p.Ordinal];
                geometry=new(m.Positions.Select(v=>Vector3.Transform(v,transform)).ToImmutableArray(),m.Normals.Select(v=>Vector3.TransformNormal(v,transform)).ToImmutableArray());
            }
            var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
            _art[model+"/"+m.Name.ToUpperInvariant()]=new(asset with{Resource=asset.Resource.Copy().SetMeshes([m]),Geometry=geometry},new(min.X,-max.Y,max.X-min.X,max.Y-min.Y));
        }
        foreach(var p in asset.Hierarchy.Pivots)
        {var v=pose.WorldTransforms[p.Ordinal].Translation;_conversationAnchors[model+"/"+p.Name.ToUpperInvariant()]=new(320+v.X,235-v.Y,100,10);}
        if(model is "tsm01a" or "tsm03a" or "jnews00" && _terminalLabels.TryGetValue(model,out var labels))
            foreach(var label in labels.Where(l=>model=="jnews00"
                ? IsJobRowLabel(l.Properties.GetValueOrDefault("Attach",""))
                : l.Properties.GetValueOrDefault("Attach","").StartsWith("desc",StringComparison.OrdinalIgnoreCase)))
                _terminalListStyles[model+"/"+label.Properties["Attach"].ToUpperInvariant()]=EnbAttachedTextStyle.Load(
                    _assets!,asset,label,Matrix4x4.Identity,pose.WorldTransforms);
        if(model is "tsm01a" or "tsm02a" or "tsm03a" or "jnews00")LoadTerminalOrdinaryText(model,asset,pose.WorldTransforms);
        _terminalPose[model]=expanded;
    }
    private void ClearTerminal()
    {
        _terminalEntering = null;
        if(_reloadItem is {} pending && IsTerminalMove(pending))_reloadItem=null;
        _manufacturingSlotColors.Clear();
        _terminalOrdinaryAlpha.Clear();_terminalOrdinaryScroll.Clear();
        _terminalOrdinaryWipes.Clear();
        _characterTextRows.Remove("WRAP_DETAIL_JNEWS00");_jobDetailSource=null;_jobDetailTextOwner=new();
        _jobLabelRefresh=null;_jobFilterOpen=0;
        if(_terminalOpenedModernInventory){_terminalOpenedModernInventory=false;CloseModernInventory();}
        if(_jobSearch is not null)_jobSearch.IsVisible=false;
        _terminalListHover.Clear();
        _terminalListScroll.Clear();_terminalListRefresh=null;_terminalScroll=0;_terminalScrollMaximum=0;_terminalRepeat=0;_terminalSelectedFormula=_terminalRememberedFormula=-1;_terminalFormulaSource=null;
        long generation = ++_terminalGeneration;
        if (_stationBrowser is {} browser)
        {
            _stationBrowser = null;
            browser.Dispose();

        }
        var owner = _terminal; _terminal = null; _jobsSeen.Clear();
        ResetCustomization();
        if (owner is not null && TerminalRequested is not null) _ = CloseTerminalRequest(owner, generation);
    }
    private async Task CloseTerminalRequest(EnbStationTerminalOwner owner, long generation)
    {
        try { await TerminalRequested!(new(owner, EnbTerminalCommand.Close)); }
        catch (Exception) { if (generation == _terminalGeneration && _terminal is null && session() == owner.Character) _message = "The terminal could not be closed."; }
    }
    private Task SendTerminal(Func<Task<bool>> action)
    {
        long generation = _terminalGeneration;
        var owner = _terminal;
        return Send(action, () => generation == _terminalGeneration && owner == _terminal);
    }
    private void UpdateTerminal()
    {
        if (_terminal is not {} owner) return;
        if (_scene?.ActiveStarbaseId != owner.StarbaseId || (_scene.LocationTransition?.Version ?? 0) != owner.HandoffVersion || session() != owner.Character)
        { ClearTerminal(); return; }
        bool resultPresented = false;
        if (owner.Type < 3 && Terminal?.Result is {} result && result.Revision != _terminalResultSeen)
        {
            _terminalResultSeen = result.Revision; resultPresented = true;
            bool failed = owner.Type == 1 && result.Validity is Net7ManufacturingValidity.AttemptTotalFailure or Net7ManufacturingValidity.AttemptNearSuccess;
            InteractionSoundRequested?.Invoke(failed ? "failure_1.wav" : "success_1.wav");
        }
        UpdateManufacturingLabels(owner, resultPresented);
        UpdateRefineryReport(owner);
        UpdateAnalysisLabels(owner);
        if (owner.Type < 3 && !_busy && !_terminalTickPending && Environment.TickCount64 - _terminalLastTick >= 100 && TerminalRequested is not null)
            _ = TickTerminal(owner);
    }
    private async Task TickTerminal(EnbStationTerminalOwner owner)
    {
        long generation = _terminalGeneration;
        _terminalTickPending = true; _terminalLastTick = Environment.TickCount64;
        try { await TerminalRequested!(new(owner, EnbTerminalCommand.Tick)); }
        catch (Exception) { if (_terminal == owner && generation == _terminalGeneration) _message = "The terminal could not be updated."; }
        finally { _terminalTickPending = false; }
    }
    private Rect? TerminalRect(string name) => ConversationRect(_terminalArt, name);
    private void TerminalText(string name, string value, bool selected = false)
    {
        var label=_terminalLabels[_terminalArt].FirstOrDefault(l=>l.Properties.GetValueOrDefault("Attach","").Equals(name,StringComparison.OrdinalIgnoreCase));
        EnbRgb? color = selected && label?.Properties.GetValueOrDefault("AltColor") is {} colorId && _terminalColors.TryGetValue(colorId,out var alternate) ? alternate : null;
        TerminalLabel(name,value,label,color);
    }
    private void TerminalLabel(string name,string value,EnbInterfaceTextDefinition? label,EnbRgb? color=null)
    {
        if(color is null && label?.Properties.GetValueOrDefault("Color") is {} colorId && _terminalColors.TryGetValue(colorId,out var authoredColor))color=authoredColor;
        if(_terminalListStyles.TryGetValue(_terminalArt+"/"+name.ToUpperInvariant(),out var listStyle))
        {
            // Refinery's 005ECE20 copies into a 64-byte row buffer. Its authored
            // FA_REVERT has no horizontal motion, unlike manufacturing.
            if(_terminalArt=="tsm03a" && value.Length>63)value=value[..63];
            if(!_terminalListScroll.TryGetValue(name,out var scroll))_terminalListScroll[name]=scroll=new();
            if(!_terminalListHover.TryGetValue(name,out var hover))_terminalListHover[name]=hover=new();
            bool hovered=_hover=="terminal/"+name;
            float opacity=hover.Update(value,hovered,Environment.TickCount64,listStyle.BlinkSpeed,listStyle.RevertSpeed);
            int first=_images.Count;
            AttachedText("terminalLabel/"+name.ToUpperInvariant(),value,listStyle,scroll,color??new EnbRgb(1,1,1));
            for(int i=first;i<_images.Count;i++)_images[i]=_images[i] with {Opacity=opacity};
            _animatingText|=hovered || opacity<1;
            return;
        }
        if(TerminalOrdinaryText(name,value,color??new EnbRgb(1,1,1)))return;
        if(!_conversationAnchors.TryGetValue(_terminalArt+"/"+name.ToUpperInvariant(),out var anchor))return;
        string justify=label?.Properties.GetValueOrDefault("Justify","")??"CENTER,LEFT";
        double width=180;
        if(_terminal?.Type==3 && name.Length>2 && char.IsDigit(name[^1]))width=name.StartsWith("NAME",StringComparison.OrdinalIgnoreCase)?175:name.StartsWith("LEVEL",StringComparison.OrdinalIgnoreCase)?40:140;
        double x=justify.Contains("RIGHT")?anchor.X-width:justify.Contains("LEFT")?anchor.X:anchor.X-width/2;
        string fontId=label?.Properties.GetValueOrDefault("FontType","FONT_SMALL")??"FONT_SMALL";
        if(justify.Contains("RIGHT") && Font(fontId) is {} font)
        {width=font.MeasureWidth(value)*font.Scale*640/_size.Width;x=anchor.X-width;}
        Text("terminalLabel/"+name.ToUpperInvariant(),value,new(x,anchor.Y,width,anchor.Height),color,fontId,!justify.Contains("LEFT") && !justify.Contains("RIGHT"));
    }
    private void TerminalButton(string name, EnbTerminalCommand command, uint value, bool enabled = true)
    {
        if (command == EnbTerminalCommand.Close && (ManufacturingExitBlocked || AnalysisExitBlocked)) enabled = false;
        if (JobsRequestPending) enabled = false;
        if (TerminalRect(name) is not {} r) return;
        string id = command == EnbTerminalCommand.Close ? "terminal/exit" : "terminal/" + name;
        if(command == EnbTerminalCommand.Close || name.ToUpperInvariant() is "GO" or "R_STACK" or "INV")Draw(_terminalArt+"/"+name.ToUpperInvariant(),r,id,enabled && !_busy);
        if (enabled && !_busy) { Hit(id, r); _terminalActions[id] = (command, value); }
    }
    private void TerminalPanel()
    {
        if (_terminalCompact && _terminal?.Type <= 4) { _terminalActions.Clear(); ModernTerminalWindow(); Flush(); return; }
        if (_terminal?.Type is 5 or 6) { CustomizationPanel(); return; }
        SetTerminalPose(_terminalArt,Terminal is { ComponentPanelExpanded:true } panel && panel.ComponentPanelRevision!=_terminalPanelOpeningRevision);
        _terminalActions.Clear();
        foreach (var (key, art) in _art.Where(a => a.Key.StartsWith(_terminalArt + "/", StringComparison.Ordinal))
            .OrderBy(a => a.Key.Contains("/HIGHLIGHT", StringComparison.Ordinal) ? 0 : 1))
        {
            string mesh = key[(key.IndexOf('/') + 1)..];
            if (_terminal?.Type >= 3 && mesh == "EXIT") continue;
            if (mesh.StartsWith("T_LEV_OFF") && (Terminal?.Depth != 3 || _terminalRecent)) continue;
            // Retail terminal setup disables TUTORIAL through RenderObj +184(1),
            // clearing activity bit 200 (00463100); it is not an exposed control.
            if (mesh == "BOX01" && _terminalArt=="jweb00" || mesh == "TUTORIAL" || mesh.StartsWith("T_LEV_ON") || mesh.EndsWith('F') && (mesh.StartsWith("CAT") || mesh.StartsWith("SUB") || mesh.StartsWith("ITEM") || mesh.StartsWith("RECENT"))) continue;
            var r = art.Design.Translate(new(320, 240)); Draw(key, r); Hit("terminalFrame/" + key, r);
        }
        foreach (var label in _terminalLabels[_terminalArt])
        {
            string text = label.Properties.GetValueOrDefault("String0", "");
            string attach = label.Properties.GetValueOrDefault("Attach", "");
            if (IsTerminalDynamicLabel(attach)) continue;
            if (text.StartsWith("desc", StringComparison.OrdinalIgnoreCase) || text.StartsWith("var", StringComparison.OrdinalIgnoreCase) || text == "xxx") continue;
            TerminalLabel(attach,text,label);
        }
        TerminalButton(_terminal?.Type >= 3 ? "EXIT_BUTTON" : "exit", EnbTerminalCommand.Close, 0);
        if (_terminal?.Type == 4)
        {
            if (_stationBrowser is {} browser && TerminalRect("BROWSER") is {} rect)
            {
                browser.Bounds=Screen(rect);
            }
            if (_message is not null && TerminalRect("BROWSER") is {} messageRect)
                Wrap("terminalBrowserError", _message, messageRect.Deflate(8), new(1,.4f,.4f), false, "FONT_SMALL");
            Flush(); return;
        }
        if (_terminal?.Type == 3) { JobsPanel(); Flush(); return; }
        var state = Terminal; var lab = state?.Lab;
        TerminalButton("go", EnbTerminalCommand.Action, 2, _terminal?.Type == 2 ? _manufactureGoEnabled : state?.CanAct == true);
        TerminalButton("r_stack", EnbTerminalCommand.Action, 3, state?.CanRefineStack == true);
        TerminalButton("clear", EnbTerminalCommand.Action, 0);
        if(_terminal?.Type == 1) TerminalButton("INV", EnbTerminalCommand.Action, 0);
        if (lab is null) { TerminalText(_terminal?.Type==1?"result00":"result_text", _message ?? "Waiting for terminal data…"); Flush(); return; }
        for(int i=0;i<6;i++)TerminalSlot($"E{i:00}",13,i,false);
        TerminalSlot("RESULT",12,0,true);
        if (_terminal?.Type != 0) TerminalSlot("OVER",14,0,true);
        TerminalText(_terminal?.Type == 2 ? "credits01" : "cost01", lab.NegotiatedCost.ToString("N0"));
        if (_terminal?.Type == 0) TerminalText("credits01", Data?.Credits?.ToString("N0") ?? "");
        if (_terminal?.Type == 1)
        {
            bool dismantle=lab.Mode==3;
            TerminalText("go",state?.CanAct==true?(dismantle?"Dismantle":"Analyze"):"");
            TerminalText("chance00",dismantle?"Recover One:":"Success:");
            TerminalText("critical00",dismantle?"Recover All:":"Critical:");
            TerminalText("chance01", _analysisChanceText); TerminalText("critical01", _analysisCriticalText);
        }
        if (_terminal?.Type == 2)
        {
            TerminalText("diff01", _expectedQualityText);
            TerminalText("cost01", _qualityRangeText);
        }
        var statusLines = TerminalStatus(state, lab, out bool placeholder);
        if (placeholder)
        {
            var place=_terminalLabels[_terminalArt].FirstOrDefault(l=>l.Properties.GetValueOrDefault("Attach","").Equals("result_text",StringComparison.OrdinalIgnoreCase));
            if(place is not null)TerminalLabel("result_text",place.Properties.GetValueOrDefault("String0",""),place);
        }
        foreach (var (label, value) in statusLines) TerminalText(label, value);
        var rows = TerminalRows(state!, lab, 10);
        for (int i = 0; i < 10; i++)
        {
            int index = (_terminal?.Type is 0 or 2?_terminalPage:_terminalPage*10) + i;
            if (index >= rows.Count) break;
            var row = rows[index]; string name = $"desc{i:00}";
            if(_terminal?.Type==2 && !_terminalRecent && state!.Depth<3)
            {
                string column=state.Depth==0?"ITEM":state.Depth==1?"CAT":"SUB";
                name=column+(row.Value+1).ToString("00");
                string suffix=state.Depth==0?$"{row.Value}":state.Depth==1?$"{state.Primary}{row.Value}":$"{state.Primary}{state.Category}{row.Value}";
                TerminalIcon(name,suffix,row.Name,"terminal/"+name);
            }
            else TerminalText(name, row.Name,_terminal?.Type is 0 or 2 && index==_terminalSelectedFormula);
            TerminalButton(name, row.Command, row.Value, _terminal?.Type is 0 or 2 || row.Command!=EnbTerminalCommand.Formula || row.Value!=uint.MaxValue);
        }
        if (_terminal?.Type == 2)
        {
            foreach(var (name,depth) in new[]{("ITEM00",0), ("CAT00",1), ("SUB00",2)})
            {
                if(state!.Depth<depth)continue;
                TerminalButton(name,EnbTerminalCommand.Browse,(uint)depth,state!.Depth>=depth);
                string suffix=depth switch
                {0 when state.Primary>=0=>$"{state.Primary}",1 when state.Primary>=0 && state.Category>=0=>$"{state.Primary}{state.Category}",
                 2 when state.Primary>=0 && state.Category>=0 && state.Subcategory>=0=>$"{state.Primary}{state.Category}{state.Subcategory}",_=>"default"};
                TerminalIcon(name,suffix,depth==0?"Items":depth==1?"Category":"Type","terminal/"+name);
            }
            if(TerminalRect("RECENT00") is {} recent)
            {Hit("terminalRecent",recent);TerminalIcon("RECENT00","recent","Recent","terminalRecent");}
            for(int i=1;state!.Depth==3 && !_terminalRecent && i<=10;i++)
            {
                uint bits=i==10?0x1ffu:1u<<(i-1);
                bool on=(state!.Filter & bits)==bits;
                if(on && _art.ContainsKey($"{_terminalArt}/T_LEV_ON_{i:00}"))Draw($"{_terminalArt}/T_LEV_ON_{i:00}",Native($"{_terminalArt}/T_LEV_ON_{i:00}"));
                TerminalButton($"T_LEV_OFF_{i:00}",on?EnbTerminalCommand.FilterOff:EnbTerminalCommand.FilterOn,bits);
            }
        }
        Flush();TerminalSlider(rows.Count);
    }
    /// <summary>
    /// The formula terminal's list (formulas, recent attempts or manufacturing categories) and its viewport and
    /// selection upkeep, for a list showing <paramref name="visible"/> rows (the retail panel shows ten).
    /// </summary>
    private List<(string Name, EnbTerminalCommand Command, uint Value)> TerminalRows(EnbStationTerminalSnapshot state, Net7ManufacturingLabProjection lab, int visible)
    {
        var rows = new List<(string Name, EnbTerminalCommand Command, uint Value)>();
        if (_terminalRecent) rows.AddRange(lab.PreviousAttempts.TakeWhile(f=>!string.IsNullOrEmpty(f.ItemName)).Select(f=>(f.ItemName,EnbTerminalCommand.Formula,unchecked((uint)f.ItemId))));
        else if (_terminal?.Type == 2 && state!.Depth < 3)
        {
            if (state.Depth == 0)
            {
                if (!_terminalInitialMenuClosed)
                    rows.AddRange(lab.PrimaryCategories.Select((p, i) => (p.Name, EnbTerminalCommand.Primary, (uint)i)).Where(r => !string.IsNullOrWhiteSpace(r.Name)));
            }
            else if ((uint)state.Primary < lab.PrimaryCategories.Count)
            {
                var categories = lab.PrimaryCategories[state.Primary].Categories;
                if (state.Depth == 1) rows.AddRange(categories.Select((c, i) => (c.Name, EnbTerminalCommand.Category, (uint)i)).Where(r => !string.IsNullOrWhiteSpace(r.Name)));
                else if ((uint)state.Category < categories.Count)
                    rows.AddRange(categories[state.Category].Subcategories.Select((s, i) => (s, i)).Where(r => r.s.Visible)
                        .Select(r => (r.s.Name, EnbTerminalCommand.Subcategory, (uint)r.i)));
            }
        }
        else rows.AddRange(lab.KnownFormula.TakeWhile(f => !string.IsNullOrEmpty(f.ItemName)).Select(f => (f.ItemName, EnbTerminalCommand.Formula, unchecked((uint)f.ItemId))));
        if(_terminal?.Type is 0 or 2)
        {
            _terminalScrollMaximum=Math.Max(0,rows.Count-visible);
            ulong revision=_terminalRecent?lab.PreviousAttemptsRevision:lab.KnownFormulaRevision;
            int materialized=_terminalRecent?lab.PreviousAttemptsState:lab.KnownFormulaState;
            var source=(_terminalRecent,state!.Depth,lab.ObjectId,materialized>=2?revision:_terminalFormulaSource?.Revision??0);
            if(_terminalFormulaSource!=source)
            {
                _terminalFormulaSource=source; _refineryDeselectPending=false;
                // 005B3260 retains the last matching remembered item and brings
                // that row into the viewport on list population.
                _terminalSelectedFormula=_terminalRememberedFormula==-1?-1:rows.FindLastIndex(r=>r.Command==EnbTerminalCommand.Formula && r.Value==unchecked((uint)_terminalRememberedFormula));
                // 005EDA10 also feeds -1 through the slider clamp when the
                // refinery selection disappears. Manufacturing skips that case.
                if(_terminalSelectedFormula>=0 || _terminal.Type==0)
                {
                    if(_terminalSelectedFormula<_terminalPage)SetTerminalScroll(_terminalSelectedFormula);
                    else if(_terminalSelectedFormula>_terminalPage+visible-1)SetTerminalScroll(_terminalSelectedFormula-(visible-1));
                }
            }
            if(_terminalPage!=(int)_terminalScroll)SetTerminalScroll(_terminalPage);
            else SetTerminalScroll(_terminalScroll);
            UpdateTerminalSliderRepeat();
        }
        else _terminalPage = Math.Clamp(_terminalPage, 0, Math.Max(0, (rows.Count - 1) / 10));
        if(_terminal?.Type is 0 or 2)
        {
            ulong revision=_terminalRecent ? lab.PreviousAttemptsRevision : lab.KnownFormulaRevision;
            int materialized=_terminalRecent ? lab.PreviousAttemptsState : lab.KnownFormulaState;
            var refresh=(_terminalPage,_terminalRecent,state!.Depth,materialized>=2?revision:_terminalListRefresh?.Revision??0);
            if(_terminalListRefresh!=refresh)
            {
                foreach(var scroll in _terminalListScroll.Values)scroll.Clear();foreach(var hover in _terminalListHover.Values)hover.Clear();_terminalListRefresh=refresh;
                if(_refineryDeselectPending){_terminalSelectedFormula=-1;_refineryDeselectPending=false;}
            }
        }
        return rows;
    }
    /// <summary>
    /// The formula terminal's result lines, by retail label (result_text, or result00/result01 for analysis): the
    /// failure, the attempt outcome, or the attempt in progress; <paramref name="placeholder"/> asks for the authored
    /// result_text prompt (nothing chosen yet).
    /// </summary>
    private List<(string Label, string Text)> TerminalStatus(EnbStationTerminalSnapshot? state, Net7ManufacturingLabProjection lab, out bool placeholder)
    {
        var lines = new List<(string Label, string Text)>();
        placeholder = false;
        string? failure = _message ?? (string.IsNullOrWhiteSpace(lab.FailureMessage) ? null : lab.FailureMessage);
        if (failure is not null) lines.Add((_terminal?.Type==1?(_message is not null?"result00":"result01"):"result_text", failure));
        else if (state?.Result is null && !lab.ManufactureTargets.Any(i=>i.IsMaterialized && i.StackCount>0)) placeholder = true;
        // 005443F0: the analysis countdown's expiry blanks Attempt/Result, so a result shows only
        // while its presentation countdown runs (INV-D78).
        bool analysisShown=_terminal?.Type!=1 || state?.ActionCountdownMilliseconds>0;
        if(_message is null && (failure is null || _terminal?.Type==1) && analysisShown && state?.Result is {} result)
        {
            string outcome=_terminal?.Type!=1?"Attempt Succeeded":result.Validity switch
            {
                Net7ManufacturingValidity.AttemptTotalFailure=>"Attempt Failed",
                Net7ManufacturingValidity.AttemptNearSuccess=>"Attempt Failed.  Item Damaged", // B797E0
                Net7ManufacturingValidity.AttemptCriticalSuccess=>"Critical Success!",
                _=>"Attempt Succeeded"
            };
            lines.Add((_terminal?.Type==1?"result00":"result_text",outcome));
            if(failure is null && _terminal?.Type==1 && result.Validity is Net7ManufacturingValidity.AttemptNormalSuccess or Net7ManufacturingValidity.AttemptCriticalSuccess)
                lines.Add(("result01",result.Mode==2?"Formula Added":"Target Dismantled"));
        }
        // 005458B0: analysis start writes Analyzing/Dismantling Target by the materialized mode.
        else if(_message is null && _terminal?.Type==1 && state is {ActionContext: Net7ManufacturingActionContext.Analyze, ActionCountdownMilliseconds: >0} && lab.ModeMaterialized && lab.Mode is 2 or 3)
            lines.Add(("result00",lab.Mode==3?"Dismantling Target":"Analyzing Target"));
        else if(failure is null && state?.ActionContext is Net7ManufacturingActionContext.Refinery or Net7ManufacturingActionContext.Manufacture)
            lines.Add(("result_text",state.ActionContext==Net7ManufacturingActionContext.Refinery?"Refining Resource":"Manufacturing Target"));
        return lines;
    }
    private void TerminalBackground()
    {
        // A compact Modern UI terminal leaves the station in view.
        if(_terminalCompact)return;
        // Keep the station visible during the one-second camera approach.
        if(Environment.TickCount64-_terminalOpenedAt<1000){_animatingText=true;return;}
        Draw("jweb00/BOX01",new(0,0,640,480));
        _draws[^1]=_draws[^1] with {BehindHud=true};
    }
    private async Task<bool> ActivateTerminal(string id)
    {
        if(ActivateCustomization(id))return true;
        if(ActivateModernJobs(id))return true;
        if (JobsRequestPending) return true;
        if(_terminal is {} recentOwner && id=="terminalRecent")
        {
            bool entering=!_terminalRecent;
            if(recentOwner.Type==2 && entering && (_busy || TerminalRequested is null))return true;
            if(ManufacturingEventIgnored(EnbTerminalCommand.Recent,0))return true;
            if(entering){_terminalRememberedFormula=_terminalSelectedFormula=-1;_terminalFormulaSource=null;}
            _terminalRecent=true;InteractionSoundRequested?.Invoke("fontclick.wav");
            // 005B16C0 case 5 runs 005B2480 and 005B4850 only when entering.
            if(entering && recentOwner.Type==2)
            {
                ResetManufacturingSelection();
                await SendTerminal(()=>TerminalRequested!(new(recentOwner,EnbTerminalCommand.Recent)));
            }
            return true;
        }
        if(_terminal?.Type==3 && id.StartsWith("jobSort/") && int.TryParse(id[8..],out int column))
        { if(_jobSortColumn==column)_jobSortDescending=!_jobSortDescending;else{_jobTertiarySort=_jobSecondarySort;_jobSecondarySort=_jobSortColumn;_jobSortColumn=column;}return true; }
        if(_terminal is not null && id.StartsWith("jobType/") && int.TryParse(id[8..],out int jobType)){if(_jobType!=jobType){_jobType=jobType;SetTerminalScroll(0);_jobSelected=null;}return true;}
        if (_terminal is not {} owner || !_terminalActions.TryGetValue(id, out var action)) return false;
        if (_busy || TerminalRequested is null) return true;
        if(action.Command!=EnbTerminalCommand.Close && ManufacturingEventIgnored(action.Command,action.Value)) return true;
        RefineryEvent(action.Command,action.Value);
        if(owner.Type==2)
        {
            // 005B1C20 (tabs) ends in 005B2480 + 005B4850; 005B2060 (category
            // rows) and the formula rows (after SendManufactureSetItemId sets
            // +BA4 = 1 for an id other than -1) end in 005B2480.
            if(action.Command==EnbTerminalCommand.Browse)ResetManufacturingSelection();
            else if(action.Command is EnbTerminalCommand.Primary or EnbTerminalCommand.Category or EnbTerminalCommand.Subcategory)RefreshManufacturingGo(Terminal);
            else if(action.Command==EnbTerminalCommand.Formula)RefreshManufacturingGo(Terminal,action.Value!=uint.MaxValue);
        }
        if(action.Command==EnbTerminalCommand.Browse){_terminalRecent=false;_terminalInitialMenuClosed=false;}
        if(action.Command==EnbTerminalCommand.JobDescription)
        {
            _jobSelected=action.Value;
            _jobSelectedRow=int.TryParse(id.AsSpan(id.Length-2),out int row)?_terminalPage+row-1:-1;
        }
        if (action.Command == EnbTerminalCommand.Close)
        {
            if (ManufacturingExitBlocked || AnalysisExitBlocked) return true;
            InteractionSoundRequested?.Invoke(owner.Type==3?"INTERFACE_MenuSlide01C.wav":"Roll_Select02b.wav");
            ClearTerminal(); _cargo = false; return true;
        }
        // Retail starts the click/action cue before queueing the request. A
        // fast result must not overtake it while the socket write completes.
        if(owner.Type<3)
        {
            string? cue=action.Command switch
            {
                EnbTerminalCommand.Action when action.Value is 2 or 3=>owner.Type==1?"analyze_1.wav":"manufacture_1.wav",
                EnbTerminalCommand.Action when owner.Type==1=>"Roll_Select02b.wav",
                EnbTerminalCommand.Formula or EnbTerminalCommand.Browse=>"fontclick.wav",
                _=>null
            };
            if(cue is not null)InteractionSoundRequested?.Invoke(cue);
        }
        // 005B1ED0 calls 005B2230 after a formula click, resetting all ten
        // row labels even if the chosen formula/text is unchanged.
        if(owner.Type is 0 or 2 && action.Command==EnbTerminalCommand.Formula)
        {
            if(id.StartsWith("terminal/desc",StringComparison.Ordinal) && int.TryParse(id.AsSpan(13),out int row))
                _terminalSelectedFormula=_terminalPage+row;
            foreach(var scroll in _terminalListScroll.Values)scroll.Clear();
            foreach(var hover in _terminalListHover.Values)hover.Clear();
            // 005B16C0 selects the row; 005B39B0 independently declines -1.
            if(action.Value==uint.MaxValue)return true;
            _terminalRememberedFormula=unchecked((int)action.Value);
        }
        await SendTerminal(() => TerminalRequested(new(owner, action.Command, action.Value)));
        if(action.Command is EnbTerminalCommand.Primary or EnbTerminalCommand.Category or EnbTerminalCommand.Subcategory or
            EnbTerminalCommand.Browse or EnbTerminalCommand.FilterOn or EnbTerminalCommand.FilterOff)_terminalPage=0;
        return true;
    }
    private void TerminalIcon(string name,string suffix,string fallback,string id)
    {
        if(TerminalRect(name) is not {} rect)return;
        string frame=_terminalArt+"/"+name.ToUpperInvariant()+"F";
        if(_art.ContainsKey(frame))Draw(frame,Native(frame));
        string texture="IM_"+suffix+".tga";
        if(Texture(texture) is not null)ImageTexture(texture,rect);
        else Text("terminalIcon/"+name,fallback,rect,new(1,1,0));
        if(_terminalTooltips?.TryGetSection("UI_MANUFACTURE",out var tips)==true && tips.TryGetValue("ICON_"+suffix,out var tip))_tips[id]=tip;
    }
    private bool IsTerminalDynamicLabel(string attach)
    {
        string a=attach.ToLowerInvariant();
        if(_terminal?.Type==3)return a=="title" || a=="accept" || a=="detail" || a=="job_title" || a=="reward_title" || a=="item_name" ||
            a.Length>4 && char.IsDigit(a[^1]) && !a.StartsWith("button");
        return _terminal?.Type==1 && a is "go" or "chance00" or "critical00" || a is "cost01" or "credits01" or "chance01" or "critical01" or "diff01" or "result00" or "result01" or "result_text";
    }
    // 688B50/689C30 refresh only the bound inventory collection; 68B5E0
    // writes .5/1 independently of 670B20's press (.35) and release (1).
    private readonly Dictionary<(uint Lab,ulong Owner,byte Inventory,int Slot), (ulong Revision,int Template,float Brightness)> _manufacturingSlotColors=[];
    private float ManufacturingSlotBrightness(EnbSceneItemSnapshot item,float? input=null)
    {
        if(Terminal?.Lab is not {} lab)return item.Count==0?.5f:1;
        var key=(lab.ObjectId,lab.CreationOrdinal,item.Inventory,item.Slot);
        ulong revision=item.Inventory==12?lab.TargetInventoryRevision:lab.ComponentInventoryRevision;
        if(!_manufacturingSlotColors.TryGetValue(key,out var state) || state.Revision!=revision || state.Template!=item.TemplateId)
            state=(revision,item.TemplateId,item.Count==0?.5f:1);
        if(input is {} brightness)state.Brightness=brightness;
        _manufacturingSlotColors[key]=state;
        return state.Brightness;
    }
    private void ManufacturingSlotInput(float brightness)
    {
        if(_terminal is not null && _pressedItem is {Inventory:12 or 13} request && Resolve(request) is {} item)
            ManufacturingSlotBrightness(item,brightness);
    }
    private void TerminalSlot(string name,byte inventory,int slot,bool writable)
    {
        if(TerminalRect(name) is not {} r)return;
        var item=Terminal?.Items.FirstOrDefault(i=>i.Inventory==inventory && i.Slot==slot);
        string id="terminalSlot/"+name;Slot(id,r,item,terminalSlot:true);
        if(item is null && inventory is 12 or 13)_tips[id]=(_inventorySlotTips?.GetValueOrDefault("NORMAL_SLOT","")??"").Replace("\\n","\n").Replace("%s",EnbRetailItemTooltip.ManufacturingDescription(null));
        if(writable)_dropSlots[id]=(inventory,slot);
        // Inventory 13 remains inspectable; BeginItemDrag and the drop map
        // exclude it from transfers.
    }
    private bool IsTerminalMove(EnbInventoryMoveRequest request)=>request.Source.Inventory is 12 or 14 || request.Destination is 12 or 14;
    private bool IsCurrentTerminalMove(EnbInventoryMoveRequest request)
    {
        // 005A0B47: a disabled analysis RESULT/OVER slot starts no transaction (INV-D73).
        if(_terminal is not {} owner || owner.Character!=request.Source.Character || request.Source.ShipOwner!=_ship || Terminal?.AnalysisTransfersInhibited==true ||
            Resolve(request.Source) is not {} source || source.Count<1 || request.Count<=0 || request.Count>request.SourceCount)return false;
        if(request.DestinationSlot<0)return request.Destination==1;
        if(request.Destination==1 && Data?.CargoSlotKeys.Contains(request.DestinationSlot)!=true)return false;
        var destination=request.Destination==1?Data?.Find(1,request.DestinationSlot):Terminal?.Items.FirstOrDefault(i=>i.Inventory==request.Destination && i.Slot==request.DestinationSlot);
        return destination?.TemplateId==request.DestinationTemplate;
    }
    private async Task<bool> DropTerminalItem(EnbInventoryMoveRequest request,Point point)
    {
        if(!IsTerminalMove(request))return false;
        if(TerminalRequested is null || !IsCurrentTerminalMove(request))return true;
        // 005A0D00 -> 00690AA0 also governs terminal inventory 12/14.
        var selection=EnbInventoryDragQuantity.Resolve(request.SourceCount,
            (_itemPressModifiers&KeyModifiers.Control)!=0,(_itemPressModifiers&KeyModifiers.Shift)!=0,_itemPressRight);
        request=request with {Count=selection.Count};
        if(selection.Prompt)
        {
            _reloadItem=request;_reloadCount=selection.Count;_quantityEditor.Text="";_quantityEditor.InsertMode=true;_quantityPointer=null;
            _reloadOrigin=new(point.X*640/_size.Width,point.Y*480/_size.Height);
            Opened?.Invoke();Publish();
        }
        else await SendTerminalMove(request);
        return true;
    }
    private async Task SendTerminalMove(EnbInventoryMoveRequest request)
    {
        if(!IsCurrentTerminalMove(request) || _terminal is not {} owner || TerminalRequested is null)return;
        await SendTerminal(()=>TerminalRequested(new(owner,EnbTerminalCommand.Move){Move=request}));
    }
}
