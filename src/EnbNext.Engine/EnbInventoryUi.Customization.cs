using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Assets;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private Net7AvatarData? _customAvatarOriginal, _customAvatar;
    private Net7ShipCustomization? _customShip;
    private Net7AvatarRecustomizeStart? _avatarCostsAtOpen;
    private Net7ShipRecustomizeStart? _shipCostsAtOpen;
    private bool CustomizationDataReady => _terminal?.Type==6
        ? _scene?.AvatarCustomization is {} ac && !ReferenceEquals(ac,_avatarCostsAtOpen)
        : _scene?.ShipCustomization is {} sc && !ReferenceEquals(sc,_shipCostsAtOpen);
    private readonly Dictionary<string,Action> _customActions=[];
    private int _customTab, _customColor=-1;
    private bool _customConfirm;
    private bool _customInFrontOfChat;
    internal void PutCustomizationBehindChat()
    {
        if(!_customInFrontOfChat)return;
        _customInFrontOfChat=false;
        Publish();
    }
    private float _customHeading;
    private bool _customNameEditing;
    internal bool CustomizationNameEditing => _customNameEditing && _terminal?.Type==5;
    private EnbUiTextInput? _customNameInput;
    private void EndCustomizationName()
    {
        _customNameEditing=false;
        if(_customNameInput is not null)_customNameInput.IsVisible=false;
    }
    private void BeginCustomizationName()
    {
        if(_customShip is null)return;
        if(_customNameInput is null)
        {
            _customNameInput=CreateTextInput(new(25),key=>Task.FromResult(CustomizationKey(key)));
            _customNameInput.Changed+=(_,_)=>
            {
                if(!_customNameEditing || _customShip is null)return;
                if(_customShip.ShipName!=_customNameInput.Text)_customShip=_customShip with {ShipName=_customNameInput.Text};
                Publish();
            };
        }
        _customNameEditing=true;
        _customNameInput.IsVisible=true;
        _customNameInput.Text=_customShip.ShipName;
        _customNameInput.CaretIndex=_customShip.ShipName.Length;
        _customNameInput.Focus();
    }
    public bool CustomizationOpen => _terminal?.Type is 5 or 6;
    private int _customShipPage;
    private EnbRetailAssetBundle? _customModelAssets;
    private readonly Dictionary<int,string> _customModelNames=[];
    private readonly Dictionary<string,int> _customModelIds=new(StringComparer.OrdinalIgnoreCase);
    private EnbRetailIniDocument? _customAvatarDefinitions;

    private void ResetCustomization()
    {
        _customAvatarOriginal=_customAvatar=null; _customShip=null;
        _avatarCostsAtOpen=_scene?.AvatarCustomization; _shipCostsAtOpen=_scene?.ShipCustomization;
        _customTab=1; _customColor=3; _customConfirm=false; _customHeading=0;_customInFrontOfChat=false;
        _customActions.Clear();_customView=new(0,0,1);_customDecalPage=0;_customPitch=0;_customPalettePoint=new(.5f);_customPaletteSelection=null;
        _customNameEditing=false;if(_customNameInput is not null){_customNameInput.IsVisible=false;_customNameInput.Editor.InsertMode=true;}
        _customShipPage=0;
        _customMotionTick=0;_customDragVelocity=Vector2.Zero;_customShipSpin=0;_customRotation=Quaternion.Identity;_customSwatchTurns.Clear();
    }

    private async Task SubmitCustomization()
    {
        if(_busy || TerminalRequested is null || _terminal is not {} owner)return;
        bool sent=false;
        await SendTerminal(async()=>sent=await TerminalRequested(new(owner,EnbTerminalCommand.Customize){Avatar=_customAvatar,Ship=_customShip}));
        if(_terminal!=owner)return;
        _customConfirm=false;
        if(sent) { ClearTerminal(); _cargo=false; }
        Publish();
    }

    private (bool Movable,int Scale) TattooOptions(byte tattoo)
    {
        _customAvatarDefinitions??=EnbRetailIniDocument.Load(Path.GetFullPath(Path.Combine(_assets!.Paths.ClientIniDirectory,"../../common/avatardef.ini")));
        _customAvatarDefinitions.TryGetSection($"FacialFeature-{tattoo:00}",out var section);
        bool movable=section?.GetValueOrDefault("Movable") is {} value && (value.Equals("true",StringComparison.OrdinalIgnoreCase) || value=="1");
        int max=int.TryParse(section?.GetValueOrDefault("Scale"),out int scale)?scale:1;
        return(movable,max);
    }

    private void SetTattoo(byte tattoo)
    {
        var a=_customAvatar!;var options=TattooOptions(tattoo);
        _customAvatar=a with {TattooTextureNum=tattoo,TattooOffset=[options.Movable?a.TattooOffset[0]:.5f,
            options.Movable?a.TattooOffset[1]:.5f,options.Scale>1?Math.Clamp(a.TattooOffset[2],.5f,options.Scale):1]};
    }

    private void SetAvatarColor(Vector3 c)
    {
        float[] rgb=[c.X,c.Y,c.Z];var a=_customAvatar!;
        _customAvatar=_customColor switch {
            0=>a with {HairColor=rgb,BeardColor=(float[])rgb.Clone()},1=>a with {EyeColor=rgb},2=>a with {SkinColor=rgb},
            3=>a with {ShirtPrimaryColor=rgb,ShirtPrimaryMetal=uint.MaxValue},4=>a with {ShirtSecondaryColor=rgb,ShirtSecondaryMetal=uint.MaxValue},
            5=>a with {PantsPrimaryColor=rgb,PantsPrimaryMetal=uint.MaxValue},6=>a with {PantsSecondaryColor=rgb,PantsSecondaryMetal=uint.MaxValue},_=>a};
    }

    private readonly Dictionary<ushort,string> _customDialogKeys = [];

    internal bool CustomizationKey(ushort key)
    {
        if(_customConfirm && _terminal?.Type is 5 or 6)
        {
            // Retail 00588480 registers only the displayed dialog's KeyN
            // entries. An unbound Escape reaches Quit, retaining this dialog.
            if(_customDialogKeys.TryGetValue(key,out var action))ActivateCustomization(action);
            else if(key==EnbInventoryKeys.Escape)return false;
            Publish();return true;
        }
        if(!_customNameEditing || _terminal?.Type!=5)return false;
        if(key==EnbInventoryKeys.Insert){_customNameInput!.Editor.InsertMode=!_customNameInput.Editor.InsertMode;Publish();return true;}
        // Win32 suppresses WM_CHAR after a handled KeyDown. The engine buffer owns
        // ordinary typing/editing keys; only editor commands are consumed here.
        if(key is not (EnbInventoryKeys.Escape or EnbInventoryKeys.Enter or EnbInventoryKeys.Tab))return false;
        if(!_busy && !_customConfirm)
        {
            EndCustomizationName();
            Publish();
        }
        return true;
    }

    private readonly EnbSceneFrame _customPreviewScene=new();
    private readonly EnbRenderObjectState _customPreviewShip=new();
    private IEnbRenderObject[]? _customPreviewObjects;
    private IEnbSceneFrame? CustomizationShipPreview()
    {
        if(_scene?.Objects.FirstOrDefault(o=>o.IsLocalShip) is not {} sourceShip || _customShip is not {} edit)return null;
        var ship=_customPreviewShip;ship.AdoptDefinition(sourceShip);
        var colors=edit.PartAttributes.Select(p=>new EnbSceneColorizationSnapshot(p.DwordValue==uint.MaxValue && p.ByteValue!=0?uint.MaxValue-1:p.DwordValue,
            new[]{p.DwordValue==uint.MaxValue?p.Color:Vector3.One}.SelectMany(v=>new[]{v.X,v.Y,v.Z}).SelectMany(BitConverter.GetBytes).ToImmutableArray())).ToImmutableArray();
        ship.ColorizationEntries=colors;ship.ColorizationAudit=null;
        ship.NameDecal=new(edit.ShipName,System.Text.Encoding.Latin1.GetBytes(edit.ShipName).ToImmutableArray(),edit.ShipNameColor);
        // 00621390 assigns the selected insignia to slots 1 and 2, opaque white.
        var decalColor=Enumerable.Repeat(1f,4).SelectMany(BitConverter.GetBytes).ToImmutableArray();
        ship.Decals=ship.Decals.Where(d=>d.Field0 is not (1 or 2)).Concat(new[]{
            new EnbSceneDecalSnapshot(1,edit.SelectedDecal,decalColor),new EnbSceneDecalSnapshot(2,edit.SelectedDecal,decalColor)}).ToImmutableArray();
        LoadCustomizationModelNames();
        int StyleAsset(int id,char part,uint style)
        {
            if(!_customModelNames.TryGetValue(id,out var name) || name.Length!=5 || char.ToUpperInvariant(name[2])!=part)return id;
            string replacement=name[..3]+(style+1)+name[4];
            return _customModelIds.GetValueOrDefault(replacement,id);
        }
        ship.BaseAsset=StyleAsset(ship.BaseAsset,'H',edit.HullStyle);ship.BaseAssetReference=null;ship.ClientBaseAssetReference=null;
        ship.SubParts=ship.SubParts.Select(p=>p with {PartValue=(uint)StyleAsset((int)p.PartValue,'W',edit.WingStyle)}).ToImmutableArray();
        ship.ShipLegos=ship.ShipLegos is {} lego?lego with {Attachments=lego.Attachments.Select(p=>p with {BaseAsset=StyleAsset(p.BaseAsset,'W',edit.WingStyle)}).ToImmutableArray()}:null;
        _customPreviewScene.AdoptMetadata(_scene);_customPreviewScene.Objects=_customPreviewObjects??=[ship];
        _customPreviewScene.Environment=_scene.Environment??new(-1,false,0,[],null,0,0,false,false,0,0,null,0);
        return _customPreviewScene;
    }

    private void LoadCustomizationModelNames()
    {
        if(ReferenceEquals(_customModelAssets,_assets))return;
        _customModelAssets=_assets;_customModelNames.Clear();_customModelIds.Clear();
        if(_assets is null)return;
        var states=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"rs.ini"));
        var bases=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"basset.ini"));
        foreach(var (section,values) in bases.EnumerateSections())
        {
            if(!EnbRetailIniDocument.TryParseSectionId(section,"BASE-",out int id))continue;
            string? file=values.GetValueOrDefault("FILE");
            if(file is null && values.GetValueOrDefault("RSLID") is {} rsl && states.TryGetSection(rsl+"\\STATE0",out var state))file=state.GetValueOrDefault("FILE");
            if(file is null)continue;
            string name=Path.GetFileNameWithoutExtension(file);
            if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^[sS][tTjJpP][hHwW][1-3][1-9]$"))continue;
            _customModelNames[id]=name;_customModelIds.TryAdd(name,id);
        }
    }
}








