using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private int _customDecalPage;
    private Vector3 _customView=new(0,0,1);
    private float _customPitch;
    private string? _customPaletteTexture;
    private Action<Vector3>? _customPaletteChoose;
    private Vector2 _customPalettePoint=new(.5f);
    private (string Texture,int Channel,Vector3 Color)? _customPaletteSelection;
    private readonly HashSet<string> _customFlashing=new(StringComparer.OrdinalIgnoreCase);

    // Geometry and text anchors belong to the installed BUY_* W3Ds and FA_* INIs.
    private void CustomArt(string mesh, Action? action=null, bool selected=false)
    {
        if (TerminalRect(mesh) is not {} rect) return;
        string id="custom/"+mesh;
        Draw(_terminalArt+"/"+mesh,rect,action is null?null:id,!_busy&&(!_customConfirm||action is null),selected);
        _draws[^1]=_draws[^1] with {FlashHighlight=selected&&_hover!=id,SpinSwatch=mesh.StartsWith("METAL",StringComparison.Ordinal),AnimateMaterials=true,SwatchAngle=mesh.StartsWith("METAL",StringComparison.Ordinal)?SwatchAngle(mesh,selected):0};
        if(_draws[^1].SpinSwatch)
        {
            // The retail shared perspective camera moves the projected edges
            // beyond the static mesh rectangle as a swatch turns.
            var draw=_draws[^1];var b=draw.Bounds;var d=draw.DesignBounds;
            _draws[^1]=draw with {Bounds=new(b.X-b.Width,b.Y-b.Height,b.Width*3,b.Height*3),DesignBounds=new(d.X-d.Width,d.Y-d.Height,d.Width*3,d.Height*3)};
        }
        if(_hover==id)_customFlashing.Add(mesh);
        if(action is not null && !_busy)_customActions[id]=action;
    }

    private void CustomLabel(string attach,string? value=null)
    {
        var label=_terminalLabels[_terminalArt].FirstOrDefault(l=>l.Properties.GetValueOrDefault("Attach","").Equals(attach,StringComparison.OrdinalIgnoreCase));
        if(label is not null)
        {
            EnbRgb? color=null;
            if(_customFlashing.Contains(attach) && label.Properties.GetValueOrDefault("MouseOver")=="FA_BLINK")
            {float pulse=MathF.Round(Math.Abs((Environment.TickCount64%500)*.004f-1)*32)/32;color=new(pulse,pulse,pulse);}
            if(_customConfirm)color=new(.35f,.35f,.35f);
            TerminalLabel(attach,value??label.Properties.GetValueOrDefault("String0",""),label,color);
        }
    }

    private bool ActivateCustomization(string id)
    {
        if(CustomizationOpen && id.StartsWith("custom/",StringComparison.Ordinal))_customInFrontOfChat=true;
        if(id=="custom/COLOR_PALETTE"){ChooseCustomizationColor(_point);return true;}
        if(id=="custom/HEAD_AREA"){MoveCustomizationTattoo(_point);return true;}
        if(_customConfirm && id is not ("custom/yes" or "custom/no"))return id.StartsWith("custom/");
        if(!_customActions.TryGetValue(id,out var action))return false;
        if(!_busy)
        {
            string cue=id is "custom/BODY_TAB" or "custom/HEAD_TAB" or "custom/CANCEL" or "custom/REVIEW" or "custom/REVIEW_CHANGE" or "custom/CONFIRM" or "custom/ENTER_NAME" ? "Login_Mouse_Click" : "ButtonClick02.wav";
            action();InteractionSoundRequested?.Invoke(cue);
        }
        return true;
    }

    private void CustomizationPanel()
    {
        TickCustomization();_animatingText=true;
        _customFlashing.Clear();
        _customActions.Clear();_customPaletteChoose=null;
        bool avatar=_terminal!.Type==6;
        string model=avatar?"buy_avatar00":new[]{"buy_ship01","buy_ship02","buy_ship03","buy_ship04"}[_customShipPage];
        LoadTerminal(model);_terminalArt=model;
        int first=_draws.Count;
        Hit("customFrame",new(0,0,640,480));
        Hit("custom/VIEW",new(0,0,640,480));
        Draw("jweb00/BOX01",new(0,0,640,480));
        _draws[^1]=_draws[^1] with {ClearBackground=true};
        CustomArt(avatar?"BACKGROUND":"BOX01");
        CustomArt("GRID");CustomArt("DIAL");
        if(avatar && _customAvatar is null && (_scene?.StationAvatars.FirstOrDefault(a=>a.AvatarId==_scene.ClientAvatarId)?.Avatar ??
            _scene?.Objects.FirstOrDefault(o=>o.IsLocalShip)?.Avatar) is {} identity)
            _customAvatarOriginal=_customAvatar=EnbAvatarCustomizationProjection.ToWire(identity);
        bool ready=CustomizationDataReady && (!avatar || _customAvatar is not null);
        if(!avatar && ready)_customShip??=_scene!.ShipCustomization!.Customization;
        if(!avatar && ready && !_customConfirm)_animatingText=true;
        // VIEW is a camera aperture, not the authored BLACK placeholder surface.
        if(ready && TerminalRect("VIEW") is {} view && _draws.Count>first)
        {
            view=new Rect(0,0,640,480); // 0054C47B/C483 explicitly pass 640 by 480.
            if(avatar)_draws[^1]=_draws[^1] with {Portrait=EnbAvatarCustomizationProjection.ToScene(_customAvatar!),PortraitEnvironment=_scene?.Environment,PortraitBounds=(Screen(view)),PortraitFullBody=_customTab==1,PortraitHeading=_customHeading,CustomizationView=_customView};
            else _draws[^1]=_draws[^1] with {EquipmentPreview=CustomizationShipPreview(),EquipmentBounds=(Screen(view)),EquipmentTransparent=true,EquipmentView=new(_customHeading,_customPitch,_customView.Z),EquipmentPan=new(_customView.X,_customView.Y),EquipmentSpin=_customShipSpin,EquipmentRotation=_customRotation};
            _draws[^1]=_draws[^1] with {PreviewDistance=distance=>_customDistance=distance,PreviewPanOrigin=origin=>_customPanOrigin=origin};
        }
        CustomArt("CANCEL",CustomizationDataReady?()=>{ClearTerminal();Publish();}:null);CustomLabel("cancel");
        if(ready)
        {
            int cost=avatar?Net7Recustomization.AvatarCost(_customAvatarOriginal!,_customAvatar!,_scene!.AvatarCustomization!.OptionDwords)
                :Net7Recustomization.ShipCost(_scene!.ShipCustomization!.Customization,_customShip!,_scene.ShipCustomization.AuxiliaryDwords);
            string accept=avatar?"REVIEW_CHANGE":_customShipPage==0?"REVIEW":_customShipPage==3?"CONFIRM":"REVIEW_CHANGE";
            CustomArt(accept,()=>{EndCustomizationName();_customConfirm=true;});
            CustomLabel(accept.ToLowerInvariant());
            CustomLabel("cost",$"Cost: {cost:N0}");CustomLabel("credits",$"Credits: {Data?.Credits:N0}");
            if(avatar)AvatarCustomizationPanel();else ShipCustomizationPanel();
            CustomArt("TURN_LEFT",()=>{});CustomArt("TURN_RIGHT",()=>{});
            CustomArt("TURN",()=>{_customHeading=0;_customPitch=0;_customRotation=Quaternion.Identity;_customDragVelocity=Vector2.Zero;});
            if(!avatar){CustomArt("TURN_UP",()=>{});CustomArt("TURN_DOWN",()=>{});}
            CustomArt("ZOOM_IN",()=>{});CustomArt("ZOOM_OUT",()=>{});
            CustomArt("PAN",()=>_customView=new(0,0,_customView.Z));
            CustomArt("PAN_LEFT",()=>{});CustomArt("PAN_RIGHT",()=>{});
            CustomArt("PAN_UP",()=>{});CustomArt("PAN_DOWN",()=>{});
            if(_customConfirm)
            {
                Flush();_customActions.Clear();_customPaletteChoose=null;
                Hit("customConfirmFrame",new(0,0,640,480));
                string section=cost==0?"NO_CHANGES":Data?.Credits is not {} funds || funds<(ulong)cost?"NEED_FUNDS":avatar?"AVATAR_CONFIRMATION":"SHIP_CONFIRMATION";
                var ini=EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"dialogbox.ini"));
                ini.TryGetSection(section,out var dialog);
                _customDialogKeys.Clear();
                for(int index=0;index<2;index++)
                    if(dialog?.ContainsKey("Option"+index)==true &&
                       ParseRetailKey(dialog.GetValueOrDefault("Key"+index,"")) is {} key)
                        _customDialogKeys[key]=index==0?"custom/yes":"custom/no";
                string prompt=section.EndsWith("CONFIRMATION")?$"Total changes cost {cost} credits. Proceed with update?":dialog?.GetValueOrDefault("Header0","")??"";
                StandardConfirmationDialog("custom", prompt, dialog, dialog?.ContainsKey("Option1")==true ? ["yes", "no"] : ["yes"]);
                _customActions["custom/yes"]=section.EndsWith("CONFIRMATION")?()=>_=SubmitCustomization():()=>_customConfirm=false;
                if(dialog?.ContainsKey("Option1")==true)_customActions["custom/no"]=()=>_customConfirm=false;
            }
        }
        if(_message is not null)CustomLabel("cost",_message);
        Flush();
        // Retail resize brings chat forward; clicking customization raises its
        // controls again. The full-screen backdrop always stays behind chat.
        for(int i=first;i<_draws.Count;i++)_draws[i]=_draws[i] with {BehindHud=!_customInFrontOfChat||_draws[i].ClearBackground};
    }

    private void AvatarCustomizationPanel()
    {
        var a=_customAvatar!;
        // FRAME_COLOR includes a translucent fill across the metal samples.
        // Draw it before the samples, otherwise it attenuates their reflections.
        CustomArt("FRAME_COLOR");
        CustomArt("BODY_TAB",()=>{_customTab=1;_customColor=3;_customHeading=0;_customView=new(0,0,1);},_customTab==1);
        CustomArt("HEAD_TAB",()=>{_customTab=0;_customColor=0;_customHeading=0;_customView=new(0,0,1);},_customTab==0);
        CustomLabel("body_tab");CustomLabel("head_tab");CustomLabel("name",(a.FirstName+" "+a.LastName).Trim());
        void Color(string mesh,int channel){CustomArt(mesh,()=>_customColor=channel,_customColor==channel);CustomLabel(mesh);}
        if(_customTab==1)
        {
            CustomLabel("cloth_title");CustomLabel("shirt_style");CustomLabel("pant_style");
            AvatarStyle("SHIRT_STYLE_LEF","SHIRT_STYLE_RIG",a.BodyType,"SHIRT",v=>_customAvatar=a with {BodyType=v});
            AvatarStyle("PANT_STYLE_LEFT","PANT_STYLE_RIGH",a.PantsType,"PANTS",v=>_customAvatar=a with {PantsType=v});
            Color("P_SHIRT_COLOR",3);Color("S_SHIRT_COLOR",4);Color("P_PANT_COLOR",5);Color("S_PANT_COLOR",6);
            CustomLabel("metals");
            uint selectedMetal=_customColor switch {3=>a.ShirtPrimaryMetal,4=>a.ShirtSecondaryMetal,5=>a.PantsPrimaryMetal,6=>a.PantsSecondaryMetal,_=>uint.MaxValue};
            for(uint i=0;i<3;i++){uint metal=i;CustomArt($"METAL{i:00}",()=>_customAvatar=_customColor switch {
                3=>a with {ShirtPrimaryMetal=metal},4=>a with {ShirtSecondaryMetal=metal},5=>a with {PantsPrimaryMetal=metal},6=>a with {PantsSecondaryMetal=metal},_=>a},selectedMetal==metal);}
        }
        else
        {
            foreach(string label in new[]{"hair_title","acc_title","hair","tattoo","eye","ear"})CustomLabel(label);
            AvatarStyle("HAIR_LEFT","HAIR_RIGHT",a.HairNum,"HAIR",v=>_customAvatar=a with {HairNum=v});
            if(a.Gender!=1){CustomLabel("beard");AvatarStyle("BEARD_LEFT","BEARD_RIGHT",a.BeardNum,"BEARD",v=>_customAvatar=a with {BeardNum=v});}
            AvatarStyle("EYE_LEFT","EYE_RIGHT",a.GoggleNum,"GOGGLE",v=>_customAvatar=a with {GoggleNum=v});
            AvatarStyle("EAR_LEFT","EAR_RIGHT",a.EarNum,"EAR",v=>_customAvatar=a with {EarNum=v});
            AvatarStyle("TATTOO_LEFT","TATTOO_RIGHT",a.TattooTextureNum,"TATTOO",SetTattoo);
            Color("HAIR_COLOR",0);Color("P_EYE_COLOR",1);Color("P_SKIN_COLOR",2);
            var options=TattooOptions(a.TattooTextureNum);
            if(options.Movable)
            {
                CustomArt("HEAD_AREA",()=>MoveCustomizationTattoo(_point));
                if(TerminalRect("HEAD_AREA") is {} area && TerminalRect("HEAD_PULL") is {} pull)
                    Draw(_terminalArt+"/HEAD_PULL",new(area.X+a.TattooOffset[0]*area.Width-pull.Width/2,area.Y+a.TattooOffset[1]*area.Height-pull.Height/2,pull.Width,pull.Height));
            }
            if(options.Scale>1)
            {
                CustomArt("SCALE_IN",()=>{});CustomArt("SCALE_OUT",()=>{});
            }
        }
        CustomPalette(_customColor==2?"fa_skin_color.tga":"fa_avatar_color.tga",SetAvatarColor);
    }

    private void AvatarStyle(string left,string right,byte value,string kind,Action<byte> change)
    {
        var a=_customAvatar!;string prefix=(a.Gender==0?"AM":"AF")+new[]{"T","J","P"}[a.Race];int max=2;
        if(kind is not ("SHIRT" or "PANTS"))
        {max=0;while(max<127 && _assets!.RetailMixResources!.TryLocateResource(kind=="TATTOO"?$"atattoo{max+1:00}.tga":$"{prefix}{kind}{max+1:00}.w3d",out _))max++;}
        int min=kind=="HAIR" && a.Race==1?1:0;
        if(max<min)return;
        CustomArt(left,()=>change((byte)(value<=min?max:value-1)));CustomArt(right,()=>change((byte)(value>=max?min:value+1)));
    }

    private void CustomPalette(string texture,Action<Vector3> choose)
    {
        _customPaletteTexture=texture;_customPaletteChoose=choose;
        SyncCustomizationPalette(texture);
        if(TerminalRect("COLOR_PALETTE") is not {} rect)return;
        string source=_terminalArt+"/COLOR_PALETTE",key=source+"/"+texture;
        if(!_art.ContainsKey(key) && _art.TryGetValue(source,out var art))
        {
            EnbW3dRenderTextureBinding Replace(EnbW3dRenderTextureBinding t)=>t with {LogicalName=texture,Location=null,ResolveLogicalNameAtRuntime=true};
            var resource=art.Asset.Resource.Copy().SetMeshes(art.Asset.Resource.Meshes.Select(m=>(m).Copy().SetMaterialPasses(m.MaterialPasses.Select(p=>(p).Copy().SetTextureStages(p.TextureStages.Select((s,i)=>i==0?(s).Copy().SetTextures(s.Textures.Select(Replace).ToArray()):s).ToArray()).SetTriangleBindings(p.TriangleBindings.Select(b=>b with {Textures=b.Textures.Select((t,i)=>i==0 && t is not null?Replace(t):t).ToArray()}).ToArray())).ToArray())).ToArray());
            _art[key]=new(art.Asset with {Resource=resource},art.Design);
        }
        Draw(key,rect);Hit("custom/COLOR_PALETTE",rect);
        Flush();
        if(TerminalRect("PULL_COLOR") is {} pull)
            Draw(_terminalArt+"/PULL_COLOR",new(rect.X+rect.Width*_customPalettePoint.X-pull.Width/2,rect.Y+rect.Height*_customPalettePoint.Y-pull.Height/2,pull.Width,pull.Height));
    }

    private void MoveCustomizationTattoo(Point point)
    {
        if(_busy || _customConfirm || _customAvatar is not {} a || !TattooOptions(a.TattooTextureNum).Movable || TerminalRect("HEAD_AREA") is not {} area)return;
        var r=Screen(area);var p=a.TattooOffset.ToArray();p[0]=(float)Math.Clamp((point.X-r.X)/r.Width,0,1);p[1]=(float)Math.Clamp((point.Y-r.Y)/r.Height,0,1);_customAvatar=a with {TattooOffset=p};
    }

    private void ChooseCustomizationColor(Point point)
    {
        if(_busy || _customPaletteChoose is null || _customPaletteTexture is null || Texture(_customPaletteTexture) is not {} image || TerminalRect("COLOR_PALETTE") is not {} design)return;
        var rect=Screen(design);
        var fraction=new Vector2((float)Math.Clamp((point.X-rect.X)/rect.Width,0,1),(float)Math.Clamp((point.Y-rect.Y)/rect.Height,0,1));
        Vector2 uv=fraction;
        if(_art.TryGetValue(_terminalArt+"/COLOR_PALETTE",out var art) && art.Asset.Geometry is {} geometry)
        {
            var mesh=art.Asset.Resource.Meshes[0];var coords=mesh.MaterialPasses[0].TextureStages[0].RuntimeTexCoords;
            var p=new Vector2((float)art.Design.X+fraction.X*(float)art.Design.Width,-(float)art.Design.Y-fraction.Y*(float)art.Design.Height);
            for(int i=0;i+2<mesh.Indices.Count;i+=3)
            {
                int ia=(int)mesh.Indices[i],ib=(int)mesh.Indices[i+1],ic=(int)mesh.Indices[i+2];
                Vector2 XY(int at)=>new(geometry.Positions[at].X,geometry.Positions[at].Y);
                var a=XY(ia);var b=XY(ib)-a;var c=XY(ic)-a;var d=p-a;float det=b.X*c.Y-b.Y*c.X;
                if(Math.Abs(det)<1e-6f)continue;
                float u=(d.X*c.Y-d.Y*c.X)/det,v=(b.X*d.Y-b.Y*d.X)/det;
                if(u<-.001f || v<-.001f || u+v>1.001f)continue;
                uv=coords[ia]*(1-u-v)+coords[ib]*u+coords[ic]*v;break;
            }
        }
        int x=Math.Clamp((int)(uv.X*image.Width),0,image.Width-1);
        int y=Math.Clamp((int)(uv.Y*image.Height),0,image.Height-1);
        int at=(y*image.Width+x)*4;
        _customPalettePoint=fraction;
        var color=new Vector3(image.BgraPixels[at+2],image.BgraPixels[at+1],image.BgraPixels[at])/255f;
        _customPaletteChoose(color);
        _customPaletteSelection=(_customPaletteTexture,_customColor,color);
    }

    private void CustomizationNameText(string name)
    {
        var resources=EnbRenderSession.Of(_assets!).ShipName;var style=resources.Style;
        if(Font(style.Font) is not {} font)return;
        double ux=_size.Width/640,uy=_size.Height/480;
        float Width(string text)=>(float)(font.MeasureWidth(text)*font.Scale/ux);
        float Advance(char c)=>(float)(font.CharacterAdvance(c)*font.Scale/ux);
        float Pixels(string text)=>(float)(font.MeasureWidth(text)*font.Scale*_scale);
        string value=_customNameEditing || name.Length!=0?name:resources.Definition.Properties.GetValueOrDefault("String0","");
        var layout=EnbRetailShipNameLayout.Resolve(value,_customNameInput?.CaretIndex??value.Length,
            _customNameEditing,_customNameInput?.Editor.InsertMode??true,
            new((float)style.Anchor.X-320,240-(float)style.Anchor.Y),style.CenteredClipBounds,
            resources.RootHalfWidth,(float)(_size.Width*_scale/640),(float)(font.CellHeight*font.Scale/uy),Width,Advance,Pixels);
        string colorId=resources.Definition.Properties[_customNameEditing?"AltColor":"Color"];
        if(!_terminalColors.TryGetValue(colorId,out var color))return;
        float width=Width(layout.Text);var clip=layout.Clip;
        if(!_labels.TryGetValue("custom/name",out var label))_labels["custom/name"]=label=new();
        label.SetText(layout.Text,font,color);label.SetDisplayScale(1,1);
        var anchor=Screen(new Rect(style.Anchor.X-width/2,style.Anchor.Y,0,0));
        var bounds=new Rect(anchor.X,anchor.Y-label.Height/2,label.Width,label.Height);
        var visible=bounds.Intersect(Screen(new Rect(clip.X+320,240-clip.Y,clip.Z-clip.X,clip.Y-clip.W)));
        if(label.Raster is {} raster && visible.Width>0 && visible.Height>0)
            _images.Add(new(raster,bounds){Clip=visible});
        if(_customNameEditing)
        {
            var caret=layout.Caret;
            _images.Add(new(StringEntryCaret,Screen(new(caret.X+320,240-caret.Y,caret.Z,caret.W))));
        }
    }

    private void ShipCustomizationPanel()
    {
        var s=_customShip!;
        if(_customShipPage is 1 or 3)CustomArt("FRAME_COLOR");
        string[] tabs=["HULL_TAB","PAINT_TAB","INSIGNIA_TAB","NAME_TAB"];
        for(int i=0;i<tabs.Length;i++){int page=i;CustomArt(tabs[i],()=>{_customShipPage=page;_customColor=page==3?8:0;EndCustomizationName();if(_customNameInput is not null)_customNameInput.Editor.InsertMode=true;},_customShipPage==i);CustomLabel(tabs[i]);}
        if(_customShipPage==0)
        {
            CustomLabel("hull00",$"Hull Style {s.HullStyle+1}");CustomLabel("wing00",$"Wing Style {s.WingStyle+1}");
            CustomArt("HULL_LEFT",()=>_customShip=s with {HullStyle=(s.HullStyle+2)%3});CustomArt("HULL_RIGHT",()=>_customShip=s with {HullStyle=(s.HullStyle+1)%3});
            CustomArt("WING_LEFT",()=>_customShip=s with {WingStyle=(s.WingStyle+2)%3});CustomArt("WING_RIGHT",()=>_customShip=s with {WingStyle=(s.WingStyle+1)%3});
        }
        else if(_customShipPage==2)
        {
            var choices=_assets!.Decals!.NormalTextures.Select(d=>(Id:(uint)d.DecalId,Resource:(string?)d.Resource)).Append((Id:0x07ffffffu,Resource:(string?)null)).ToArray();
            CustomArt("LEFT",()=>_customDecalPage=Math.Max(0,_customDecalPage-1));
            CustomArt("RIGHT",()=>_customDecalPage=Math.Min((choices.Length-1)/9,_customDecalPage+1));
            for(int i=0;i<9 && _customDecalPage*9+i<choices.Length;i++)
            {var decal=choices[_customDecalPage*9+i];string mesh=$"D{i:00}";if(TerminalRect(mesh) is {} rect){if(decal.Resource is {} texture)ImageTexture(texture,rect,decal.Id==s.SelectedDecal?new Vector3(1+MathF.Abs((Environment.TickCount64%1000)*.002f-1)):null);else CustomArt(mesh);Hit("custom/"+mesh,rect);_customActions["custom/"+mesh]=()=>_customShip=s with {SelectedDecal=decal.Id};}}
        }
        else if(_customShipPage==3)
        {
            CustomArt("ENTER_NAME",BeginCustomizationName);
            CustomizationNameText(s.ShipName);
            CustomPalette("fa_name_color.tga",rgb=>_customShip=_customShip! with {ShipNameColor=rgb});
        }
        else
        {
            void Button(string mesh,Action action,bool selected=false){CustomArt(mesh,action,selected);CustomLabel(mesh);}
            Button("PRIMARY",()=>_customColor&=~1,(_customColor&1)==0);Button("SECONDARY",()=>_customColor|=1,(_customColor&1)==1);
            string[] parts=["HULL","PROFESSION","WING","ENGINE"];
            for(int i=0;i<4;i++){int part=i;Button(parts[i],()=>_customColor=part*2+(_customColor&1),_customColor/2==i);}
            void Attribute(uint? metal=null,byte? gloss=null){var p=_customShip!.PartAttributes.ToArray();p[_customColor]=p[_customColor] with {DwordValue=metal??uint.MaxValue,ByteValue=gloss??p[_customColor].ByteValue};_customShip=_customShip with {PartAttributes=p};}
            Button("GLOSSY",()=>Attribute(gloss:1));Button("FLAT",()=>Attribute(gloss:0));CustomLabel("metals");
            for(uint i=0;i<3;i++){uint metal=i;CustomArt($"METAL{i:00}",()=>Attribute(metal),_customShip!.PartAttributes[_customColor].DwordValue==metal);}
            CustomPalette("fa_ship_color.tga",rgb=>{var p=_customShip!.PartAttributes.ToArray();p[_customColor]=p[_customColor] with {Color=rgb,DwordValue=uint.MaxValue};_customShip=_customShip with {PartAttributes=p};});
        }
    }
}






