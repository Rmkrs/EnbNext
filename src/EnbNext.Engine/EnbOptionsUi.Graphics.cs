using EnbNext.Presentation;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    private EnbNext.Engine.EnbGraphicsSettingsState _graphicsState = null!;
    private EnbNext.Engine.EnbGraphicsPanel _graphicsPanel = null!;
    private bool GraphicsOpen => _page == 6;
    public bool GraphicsPreviewActive => _graphicsState?.PreviewActive == true && (_page == 6 || _modernCategory is "graphics" or "advanced");
    // A window resize re-lays out every options page (the Modern UI Options window too); only the world frame waits.
    public bool KeepsOpenThroughResize => _page != 0 || ModernOptionsOpen;

    private string GraphicsCharacterDisplayName()
    {
        string? name = CharacterDisplayNameRequested?.Invoke()?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "Character" : name;
    }

    public void RefreshGraphicsDisplayChoice()
    {
        if (!GraphicsPreviewActive) return;
        ReadUiScale();
        _graphicsPanel.CancelInput();
        if (!_graphicsState.RefreshDisplay(CurrentDisplayRequested,DisplayChoicesRequested)) return;
        Publish();
    }

    /// <summary>The chosen graphics card could not be used: the card in use is shown again, with the reason.</summary>
    public void GraphicsAdapterFailed(string reason)
    {
        if(_graphicsState is null)return;
        _graphicsState.Adapter=GraphicsAdapterRequested?.Invoke();
        if(ModernOptionsOpen){ModernOptionsError=reason;ModernOptionsChanged?.Invoke();}else Error=reason;
        Publish();
    }

    private void SelectGraphicsAdapter(int index)
    {
        var key=_graphicsState.Adapters[index].Key;if(key==_graphicsState.Adapter)return;
        _graphicsState.Adapter=key;PreviewGraphicsAdapter?.Invoke(key);Error=null;
    }

    private void ReadUiScale()
    {
        _graphicsState.UiScale=UiScaleRequested?.Invoke();
        _graphicsState.SystemScale=SystemScaleRequested?.Invoke()??1;
        _graphicsState.UiScales=new double?[]{null,1,1.25,1.5,1.75,2}.Where(s=>s is null||
            _size.Width*_scaling/s.Value>=640&&_size.Height*_scaling/s.Value>=480).ToArray();
    }
    private void OpenGraphics(bool advanced = false)
    {
        BeginGraphics(advanced);
        _page = 6;
    }

    /// <summary>The graphics draft and its live preview (the classic page and the Modern UI Options window).</summary>
    private void BeginGraphics(bool advanced)
    {
        EnbRetailPlayerOptions options = _assets!.ResolvePlayerOptions(_owner).Options;
        _graphicsState.Advanced = advanced;
        var cap = BackgroundCapRequested?.Invoke() ?? (false, 60);
        _graphicsState.LoadBackgroundCap(cap.Enabled, cap.Fps);
        EnbRetailRenderBudgetSourceSettings source = EnbNextRenderSettings.Current.ClampLikeRetail();
        _graphicsState.Load(options,source);
        _graphicsState.Enhanced=_graphicsState.OriginalEnhanced=EnhancedGraphicsRequested?.Invoke()??EnbEnhancedGraphicsOptions.Default;
        _graphicsState.TextureOverrides=_graphicsState.OriginalTextureOverrides=TextureOverridesRequested?.Invoke();

        _graphicsState.TexturePacks=TexturePacksRequested?.Invoke()??[""];_graphicsState.TextureDropdown=false;
        _graphicsState.Adapters=GraphicsAdaptersRequested?.Invoke()??[];_graphicsState.AdapterAtRestart=GraphicsAdapterAtRestart;_graphicsState.Adapter=_graphicsState.OriginalAdapter=GraphicsAdapterRequested?.Invoke();_graphicsState.AdapterDropdown=false;
        _graphicsState.OpenDisplay(BeginDisplayPreview,CurrentDisplayScopeRequested,CurrentDisplayRequested,DisplayChoicesRequested);
        _graphicsState.BeginPreview();
        ReadUiScale();_graphicsState.UiScaleDropdown=false;
        _graphicsPanel.OpenInput();
        _graphicsState.ResolutionDropdown = false;
        Error = null;
    }

    private void ActivateGraphics(string key)
    {
        if(key is "graphics/ENHANCED" or "graphics/BLOOM" or "graphics/HDR" or "graphics/SUPERSAMPLING" or "graphics/ANISOTROPIC")
        {
            var value=_graphicsState.Enhanced;
            if(key=="graphics/ENHANCED")value=value with {Enabled=!value.Enabled};
            else if(value.Enabled)value=key=="graphics/ANISOTROPIC"?value with {AnisotropicFiltering=!value.AnisotropicFiltering}:key=="graphics/SUPERSAMPLING"?value with {Supersampling=!value.Supersampling}:key=="graphics/HDR"?value with {Hdr=!value.Hdr}:value with {Bloom=!value.Bloom};
            _graphicsState.Enhanced=value;PreviewEnhancedGraphics?.Invoke(value);return;
        }
        if(key=="graphics/BGCAP"){_graphicsState.BackgroundCapEnabled=!_graphicsState.BackgroundCapEnabled;return;}
        if(key=="graphics/ADAPTER")
        {
            _graphicsState.AdapterDropdown=!_graphicsState.AdapterDropdown;
            _graphicsState.TextureDropdown=_graphicsState.UiScaleDropdown=_graphicsState.ResolutionDropdown=false;return;
        }
        if(_graphicsState.AdapterDropdown&&key.StartsWith("graphics/choice",StringComparison.Ordinal)&&
            int.TryParse(key.AsSpan(15),out int adapterIndex)&&adapterIndex>=0&&adapterIndex<_graphicsState.Adapters.Length)
        { SelectGraphicsAdapter(adapterIndex);_graphicsState.AdapterDropdown=false;return; }
        _graphicsState.AdapterDropdown=false;
        if(key=="graphics/TEXTUREOVERRIDES")
        {
            _graphicsState.TextureDropdown=!_graphicsState.TextureDropdown;
            _graphicsState.UiScaleDropdown=false;_graphicsState.ResolutionDropdown=false;return;
        }
        if(_graphicsState.TextureDropdown&&key.StartsWith("graphics/choice",StringComparison.Ordinal)&&
            int.TryParse(key.AsSpan(15),out int packIndex)&&packIndex>=0&&packIndex<_graphicsState.TexturePacks.Length)
        {
            try { var pack=_graphicsState.TexturePacks[packIndex];PreviewTextureOverrides?.Invoke(pack);_graphicsState.TextureOverrides=TextureOverridesRequested is null?pack:TextureOverridesRequested();Error=null; }
            catch(Exception ex) when(ex is IOException or InvalidOperationException or UnauthorizedAccessException){Error=ex.Message;}
            _graphicsState.TextureDropdown=false;return;
        }
        _graphicsState.TextureDropdown=false;
        if(key=="graphics/UISCALE")
        {
            ReadUiScale();_graphicsState.UiScaleDropdown=!_graphicsState.UiScaleDropdown;
            _graphicsState.ResolutionDropdown=false;return;
        }
        if(_graphicsState.UiScaleDropdown&&key.StartsWith("graphics/choice",StringComparison.Ordinal)&&
            int.TryParse(key.AsSpan(15),out int scaleIndex)&&scaleIndex>=0&&scaleIndex<_graphicsState.UiScales.Length)
        {
            try { PreviewUiScaleRequested?.Invoke(_graphicsState.UiScales[scaleIndex]);Error=null; }
            catch(InvalidOperationException ex){Error=ex.Message;}
            _graphicsState.UiScaleDropdown=false;ReadUiScale();return;
        }
        _graphicsState.UiScaleDropdown=false;
        if (key is "graphics/scopeSystem" or "graphics/scopeCharacter")
        {
            var scope = key == "graphics/scopeSystem" ? EnbDisplayScope.System : EnbDisplayScope.Character;
            Error = _graphicsState.SelectDisplayScope(scope,SelectDisplayScopeRequested,CurrentDisplayScopeRequested,
                CurrentDisplayRequested,DisplayChoicesRequested);
            ReadUiScale();return;
        }
        if (key.StartsWith("graphics/choice", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan("graphics/choice".Length), out int choice) && choice < _graphicsState.DisplayChoices.Count)
        {
            Error = _graphicsState.PreviewDisplay(choice,PreviewDisplayRequested);
            return;
        }
        if (key == "graphics/EXIT") { _graphicsState.ResolutionDropdown = false; Close(); return; }
        if (key == "graphics/CANCEL")
        { RollbackGraphicsPreview(); _graphicsState.ResolutionDropdown = false; ReturnFromCategory(); Error = null; return; }
        if (key == "graphics/RESOLUTION")
        {
            _graphicsState.ResolutionDropdown = !_graphicsState.ResolutionDropdown;
            return;
        }
        _graphicsState.ResolutionDropdown = false;
        if (key == "graphics/CHECK00")
        { _graphicsPanel.ToggleRecommended(); PublishGraphicsPreview(); return; }
        if (key != "graphics/OK") return;

        if (ApplyGraphics()) ReturnFromCategory();
        Error = _graphicsState.Error;
    }

    /// <summary>Keeps the graphics draft: the character settings, the Next graphics choices and the display.</summary>
    private bool ApplyGraphics() => _graphicsState.TryApply(SaveGraphicsOptions,PreviewGraphicsOptions,()=>
    {
        SaveEnhancedGraphics?.Invoke(_graphicsState.Enhanced);
        _graphicsState.OriginalEnhanced=_graphicsState.Enhanced;
        CommitDisplayPreview?.Invoke();SaveTextureOverrides?.Invoke(_graphicsState.TextureOverrides);
        _graphicsState.OriginalTextureOverrides=_graphicsState.TextureOverrides;
        if(_graphicsState.Adapter!=_graphicsState.OriginalAdapter){SaveGraphicsAdapter?.Invoke(_graphicsState.Adapter);_graphicsState.OriginalAdapter=_graphicsState.Adapter;}
        if(_graphicsState.Advanced)SaveBackgroundCap?.Invoke(_graphicsState.BackgroundCapEnabled,_graphicsState.BackgroundCap);
    });

    private void ApplyNativeGraphics(string? command)
    {
        if(command=="graphics/CHECK00" || command?.StartsWith("graphics/TRACK",StringComparison.Ordinal)==true)
        { _graphicsState.ResolutionDropdown=false;_graphicsState.UiScaleDropdown=false;_graphicsState.TextureDropdown=false;_graphicsState.AdapterDropdown=false;PublishGraphicsPreview(); }
        else if(command is not null)ActivateGraphics(command);
    }

    private void PublishGraphicsPreview()
    {
        if (!_graphicsState.PreviewActive) return;
        _graphicsState.Preview(PreviewGraphicsOptions); Error = _graphicsState.Error;
    }

    private void RollbackGraphicsPreview()
    {
        if(_graphicsState?.PreviewActive==true)PreviewEnhancedGraphics?.Invoke(_graphicsState.OriginalEnhanced);
        if(_graphicsState?.PreviewActive==true&&_graphicsState.Adapter!=_graphicsState.OriginalAdapter)
        {PreviewGraphicsAdapter?.Invoke(_graphicsState.OriginalAdapter);_graphicsState.Adapter=_graphicsState.OriginalAdapter;}
        if(_graphicsState?.PreviewActive==true && _graphicsState.TextureOverrides!=_graphicsState.OriginalTextureOverrides)
            PreviewTextureOverrides?.Invoke(_graphicsState.OriginalTextureOverrides);
        _graphicsPanel?.CancelInput();_graphicsState?.Retire(PreviewGraphicsOptions,CancelDisplayPreview);
    }

    private void PublishGraphics(ImmutableList<EnbUiModelDraw>.Builder list)
        => list.AddRange(_graphicsPanel.DrawFrame(_size,_scaling,Error,
            _graphicsState.DisplayChoices,_graphicsState.DisplayChoiceIndex,_graphicsState.ResolutionDropdown,_graphicsState.DisplayScope,GraphicsCharacterDisplayName()));
}
