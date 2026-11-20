using EnbNext.Presentation;
namespace EnbNext.Engine;

internal sealed partial class EnbFrontendController
{
    // 006AD120 uses case-sensitive history shared across controller lifetimes.
    private readonly HashSet<string> _creationCueHistory = new(StringComparer.Ordinal);
    private static readonly string[] CreationIntroCues =
    ["ClassSelectionIntro", "AvatarCreationIntro", "AvatarCreationHead", "AvatarCreationBody",
     "ShipCreationGeometry", "ShipCreationColor", "ShipCreationDecal", "ShipCreationName"];

    private void SeedCreationNarrationHistory()
    {
        // 006AD4B0 imports these entries when tutorial.dat opens successfully.
        if(_introductoryTutorialCompleted)_creationCueHistory.UnionWith(CreationIntroCues);
    }

    private int[] TakeCreationIntro(string cue)
    {
        if(!_creationCueHistory.Add(cue))return [];
        // Literal filenames in 00574710, 00553AF0, 00554E80,
        // 00622180 and 00625A60; these are audio-only queue producers.
        return cue switch
        {
            "ClassSelectionIntro" => [328],
            "AvatarCreationIntro" => [347],
            "AvatarCreationHead" => [351,352,353],
            "AvatarCreationBody" => [355,356,357],
            "ShipCreationGeometry" => [300,301,302,303],
            "ShipCreationColor" => [306,307,308],
            "ShipCreationDecal" => [311],
            "ShipCreationName" => [312],
            _ => throw new ArgumentException("Unknown creation intro",nameof(cue))
        };
    }

    private void SpeakCreationIntro(string cue)
        // Producers clear both active voice and queued lines before the history
        // check (0067C7B0 -> 0043F920), even on subsequent visits.
        => SpeakCreationResources(TakeCreationIntro(cue));
}
