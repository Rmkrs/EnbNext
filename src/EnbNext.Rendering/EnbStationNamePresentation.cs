using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Current client 0063F490 name scale and range/occlusion fade.</summary>
public static class EnbStationNamePresentation
{
    // 4C1020 appends a separator only when the last name is nonempty.
    public static string FullName(EnbSceneAvatarSnapshot avatar)
        => avatar.FirstName+(avatar.LastName.Length==0?"":" "+avatar.LastName);
    // 63AC60 ->641EB0 ->74D570: valid GroupInfo, any nonempty name,
    // then ID membership across all five slots (including unnamed slots).
    public static EnbRgb RemoteNameColor(EnbSceneGroupSnapshot? group,uint id)
        => group is {AuxDataState: not 0} && group.Members.Take(5).Any(m=>m.Name.Length!=0) &&
           group.Members.Take(5).Any(m=>m.GameId==id) ? new(.66f,0,.66f) : new(.33f,.33f,.99f);
    public static double Scale(double cameraDistance) => Math.Min(2,25/Math.Max(.001,cameraDistance));
    public static float AdvanceOpacity(float opacity,double cameraDistance,bool occluded,double seconds,float fadeInSpeed,float fadeOutSpeed)
        => Math.Clamp(opacity+(float)Math.Max(0,seconds)*(Scale(cameraDistance)>=.5&&!occluded?fadeInSpeed:-fadeOutSpeed),0,1);
}
