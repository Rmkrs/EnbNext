using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbRetailDebrisRuntime[]? _debrisLayers;
    private readonly Dictionary<int, EnbResolvedTextureResource?> _debrisTextures = [];
    private readonly Dictionary<uint, Vector3> _debrisWinds = [];
    internal int DebrisSelectedLayer { get; private set; } = -1;
    internal int DebrisSampleCount { get; private set; }
    internal int DebrisPrimitiveCost { get; private set; }
    internal float DebrisBudgetCost { get; private set; }
    private EnbRetailDebrisRuntime? _preparedDebris;
    internal string? DebrisDiagnostic { get; private set; }
    internal Action<EnbW3dDrawTask>? DebrisPacketObserved { get; set; }

    private void ClearDebris()
    {
        _debrisLayers=null;_debrisTextures.Clear();_debrisWinds.Clear();
        DebrisSelectedLayer=-1;DebrisSampleCount=0;DebrisPrimitiveCost=0;DebrisDiagnostic=null;
        _preparedDebris=null;DebrisBudgetCost=0;
    }

    private void PrepareDebris(LiveFrame frame, EnbRetailAssetBundle assets)
    {
        DebrisSelectedLayer=-1;DebrisSampleCount=0;DebrisPrimitiveCost=0;DebrisDiagnostic=null;
        _preparedDebris=null;DebrisBudgetCost=0;
        // 00514010 activates in0/5, detaches in1. The embedded scanner is a separate scene.
        if(!_useMainViewEnvironment || _embeddedScanner || frame.Snapshot.Environment is not {} environment ||
            environment.PresentationMode is not (0 or 5))return;
        try
        {
            if(_debrisLayers is null)
            {
                var ini=assets.FileResources.TryRead("settings.ini",out var bytes,out _,out _)
                    ?EnbRetailIniDocument.Parse(Encoding.Latin1.GetString(bytes)):EnbRetailIniDocument.Parse("");
                _debrisLayers=EnbRetailDebrisCatalog.Read(ini).Select(d=>new EnbRetailDebrisRuntime(d)).ToArray();
            }
            if(_debrisLayers.Length==0)return;
            // 008A5C10 ->00737F00 ->00744260 ->0053DFB0. This field was already retained in the snapshot.
            uint selected=environment.RawServerParameters.Length>=36?BinaryPrimitives.ReadUInt32LittleEndian(environment.RawServerParameters.AsSpan(32,4)):0;
            var influence=InfluenceSkyPresentation;
            if(influence.OverrideActive)selected=influence.Region==EnbCurrentRetailInfluenceNebulaRegion.Room?1u:2u;
            int index=(int)Math.Min(selected,(uint)(_debrisLayers.Length-1));var layer=_debrisLayers[index];
            DebrisSelectedLayer=index;layer.Amplitude=1;
            if(environment.PresentationMode==5)
            {
                if(!_debrisWinds.TryGetValue(environment.SectorNum,out var wind))
                {
                    var ini=assets.FileResources.TryRead($"gas{environment.SectorNum:D4}.ini",out var bytes,out _,out _)
                        ?EnbRetailIniDocument.Parse(Encoding.Latin1.GetString(bytes)):EnbRetailIniDocument.Parse("");
                    ini.TryGetSection("GENERAL",out var values);
                    float F(string key)=>values is not null&&EnbRetailIniDocument.TryReadFloat(values,key,out var v)?v:0;
                    wind=new Vector3(F("WINDX"),F("WINDY"),F("WINDZ"))*3600f;_debrisWinds.Add(environment.SectorNum,wind);
                }
                float hours=(float)(frame.Snapshot.GetOrbitalTimeMilliseconds(frame.CurrentClientTime)*EnbRetailOrbitalFogEvaluator.CurrentRetailHoursPerMillisecond);
                layer.SetOffset(wind*hours);
            }
            // 00942020/00942040 consume 006B9640 before the draw-time sample replaces
            // the helper buffers. Debris is force-visible; it contributes its retained cost.
            _preparedDebris=layer;
            DebrisBudgetCost=EnbCurrentRetailFixedRenderCost.FromDefaultIntegerCost(layer.PrimitiveCost);
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or OverflowException or ArgumentException)
        {
            DebrisDiagnostic=ex.Message;
        }
    }

    private void AppendDebris(LiveFrame frame, EnbRetailAssetBundle assets, List<EnbW3dDrawTask> packets)
    {
        if(_preparedDebris is not {} layer)return;
        int index=DebrisSelectedLayer;
        try
        {
            if(!_debrisTextures.TryGetValue(index,out var texture))
            {
                texture=layer.Definition.Texture.Length==0?null:assets.Textures.ResolveCurrentRetailMissingTexture(layer.Definition.Texture,assets.Paths.ClientExecutablePath);
                _debrisTextures.Add(index,texture);
            }
            if(texture is not null && texture.Image is null)throw new InvalidDataException(texture.Diagnostic);
            var packet=layer.BuildPacket(frame.Camera,texture,frame.CurrentClientTime,index,_particleStreamFrame);
            DebrisSampleCount=layer.LastCount;DebrisPrimitiveCost=layer.PrimitiveCost;
            if(packet is not null){DebrisPacketObserved?.Invoke(packet);packets.Add(packet);}
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or OverflowException or ArgumentException)
        {
            // Optional layer failure is local. Do not invent geometry or a different selected layer.
            DebrisDiagnostic=ex.Message;
        }
    }
}
