using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbStationDoorway(string Pivot, uint? Destination, EnbW3dRenderResource Resource,
    Matrix4x4 Transform, EnbW3dRenderHierarchyAnimation? Animation)
{
    public string? AnimationDiagnostic {get;init;}
    // 0063C590: surface distance <3 with avatar radius 3 and door radius 10.
    public bool IsNear(Vector3 position) => Vector3.Distance(position,Transform.Translation)<16;

    public static ImmutableArray<EnbStationDoorway> Load(EnbRetailAssetBundle assets,
        EnbSceneStationDefinition definition, EnbCurrentRetailStationInterior room)
    {
        string prefix=definition.DefinitionValue switch {0=>"t",1=>"j",2=>"p",_=>"n"};
        string[] pivots=["DOOR_FORWARD","DOOR_LEFT","DOOR_BACK","DOOR_RIGHT"];
        var result=ImmutableArray.CreateBuilder<EnbStationDoorway>();
        var cache=new Dictionary<string,(EnbW3dRenderResource Resource,EnbW3dRenderHierarchyAnimation? Animation,string? Diagnostic)>();
        for(int i=0;i<(room.RoomType==0?1:4);i++)
        {
            int destination=EnbStationRoomLayout.Destination(room.RoomType,i);
            bool connected=destination>=0 && definition.Rooms.Any(r=>r.RoomType==destination);
            // 0063505B connected hall; 00635497/0063553D closed room-specific caps.
            string name=connected ? prefix+"bar06" : room.RoomType==0 ? (definition.DefinitionValue switch {0=>"tsd01B",1=>"jsd01B",2=>"psd01B",_=>"tsd07B"})
                : prefix+"bar"+(room.RoomType switch {1=>"08_H",2=>"08_V",_=>"08"});
            if(!cache.TryGetValue(name,out var loaded))
            {
                var catalog=assets.RetailMixResources!;
                if(!catalog.TryReadResource(name+".w3d",out var bytes,out var location) || !EnbW3dModel.TryDecode(bytes,out var model))
                    throw new InvalidDataException("Station doorway unavailable: "+name);
                var textures=model.Meshes.SelectMany(m=>m.TextureNames).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(n=>new EnbResolvedTextureReference(n,catalog.TryLocateResource(n,out var t)?t:null)).ToArray();
                if(!EnbW3dRenderResourceCompiler.TryCompile(new(location.Name,name,location,model,textures),out var resource,out var error))
                    throw new InvalidDataException(error);
                EnbW3dRenderHierarchyAnimation? animation=null;
                string? animationDiagnostic=null;
                if(connected)
                {
                    // 69F540 retains the doorway and leaves its slot unbound
                    // when optional HAnim acquisition returns null.
                    try
                    {
                        if(EnbCurrentRetailRslHAnimResolver.TryResolve(resource,new("",EnbRslFileDependencyStatus.NotAuthored),
                            assets.Paths,assets.MixResources,catalog,name+"."+name+"-1",out var resolved,out animationDiagnostic))
                        {resource=resolved.DrawResource;animation=resolved.Animation;}
                    }
                    catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
                    {animationDiagnostic=ex.Message;}
                }
                cache[name]=loaded=(resource,animation,animationDiagnostic);
            }
            result.Add(new(pivots[i],connected?(uint)destination:null,loaded.Resource,room.PivotTransform(pivots[i]),loaded.Animation)
                {AnimationDiagnostic=loaded.Diagnostic});
        }
        return result.ToImmutable();
    }
}
