using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Net7.Presentation;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    public Func<uint,uint,Task<bool>>? IncomingMessageRequested { get; set; }
    private Net7ConfirmedActionPending? _incomingMessage, _dismissedIncomingMessage;
    private EnbInterfaceTextDefinition? _incomingStyle;
    private EnbRetailIniDocument? _incomingFontArt;
    private EnbMessageFontEffects? _incomingEffects;
    private long _incomingTick;

    private void LoadIncomingMessage()
    {
        _incomingStyle=null;_incomingFontArt=null;_incomingEffects=null;
        if(_assets is null)return;
        var layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fa_tsi05b.ini"));
        if(layout.TryResolve("message_TSI05B",out var style))_incomingStyle=style;
        _incomingFontArt=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fontart.ini"));
        var asset=EnbAuthoredUiAsset.Load(_assets,"tsi05b.w3d");
        if(asset?.Resource.Meshes.FirstOrDefault(m=>m.Name=="B05") is {} mesh)
            _art["incoming"]=(asset with {Resource=asset.Resource.Copy().SetMeshes([mesh])},
                EnbCockpitUi.MeshBounds(asset,"B05").Translate(-320,-240));
    }

    private bool AdvanceIncomingMessage(Net7ConfirmedActionPending? message)
    {
        bool changed=!ReferenceEquals(message,_incomingMessage);
        if(changed)
        {
            _incomingMessage=message;_dismissedIncomingMessage=null;
            _incomingEffects=null;_incomingTick=MessageClock();
        }
        if(message is null || ReferenceEquals(message,_dismissedIncomingMessage))return changed;
        if(_incomingEffects is null && _incomingStyle is {} style && _incomingFontArt is {} art)
            EnbMessageFontEffects.TryCreate(style,art,out _incomingEffects,message.Message);
        long now=MessageClock();_incomingEffects?.Advance(Math.Max(0,now-_incomingTick));_incomingTick=now;
        return true;
    }

    private void PublishIncomingMessage()
    {
        if(_incomingMessage is not {} message || ReferenceEquals(message,_dismissedIncomingMessage) ||
            !_art.TryGetValue("incoming",out var art) || _incomingStyle is not {} style)return;
        var rect=ComboScreen(art.Bounds.Translate(320,240));
        int first=_images.Count;
        Text(message.Message,rect,_messageLines.Color(style.Properties.GetValueOrDefault("Color","FA_YELLOW"),new(1,1,0)),
            style.Properties.GetValueOrDefault("FontType","FONT_SMALL"),centerVertically:true,opacity:_incomingEffects?.Opacity??1);
        if(_images.Count>first)
        {
            var text=_images[first];
            _images[first]=text with {Bounds=text.Bounds.Translate((rect.Width-text.Bounds.Width)/2,0)};
        }
        var captions=_images.Skip(first).ToImmutableArray();
        while(_images.Count>first)_images.RemoveAt(_images.Count-1);
        _draws.Add(new(art.Asset,0,rect,false){LayoutGroup=EnbUiGroup.Chat,DesignBounds=art.Bounds,Images=captions});
        Hit("incomingMessage",rect,()=>_ = AcceptIncomingMessage(),()=>_ = AcceptIncomingMessage()); // 005629E0: mask 55, right press accepts too (CHAT-D40)
    }

    private async Task AcceptIncomingMessage()
    {
        if(_incomingMessage is not {} message || ReferenceEquals(message,_dismissedIncomingMessage) ||
            _session() is not uint character || IncomingMessageRequested is null)return;
        // 00563280 event4 -> 00750800: acceptance, then hide; the server owns the dialogue.
        _dismissedIncomingMessage=message;Publish();
        try { await IncomingMessageRequested(character,message.Id); }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException)
        { Error=ex.Message;Publish(); }
    }
}
