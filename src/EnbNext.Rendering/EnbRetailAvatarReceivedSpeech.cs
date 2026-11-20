using EnbNext.Net7.Social;

namespace EnbNext.Rendering;

/// <summary>Stationary received NLP, 00784B80 -> 00784700 -> 00782200.</summary>
public sealed class EnbRetailAvatarReceivedSpeech
{
    public int Mood { get; private set; }
    public int Posture { get; private set; }
    public int Locomotion { get; private set; } = 2;

    public IReadOnlyList<EnbRetailAvatarSpeechCommand> Consume(IEnumerable<Net7AvatarNlpCommand> records, int portraitMode, int currentMood = 0)
    {
        Mood = currentMood;
        var commands = new List<EnbRetailAvatarSpeechCommand>();
        int pendingMood = -1;
        foreach (var record in records)
        {
            int argument = unchecked((sbyte)record.Argument);
            switch (record.Type)
            {
                case 0 when Posture is not (1 or 2) && Locomotion == 2:
                    pendingMood = argument;
                    break;
                case 1 or 2 when record.Argument != 0 && Posture is not (1 or 2) && Locomotion == 2:
                    commands.Add(new("", record.Type == 1 ? record.Text : "", pendingMood) { AnimationNumber = argument });
                    pendingMood = -1;
                    break;
                case 3:
                    Mood = argument;
                    break;
                case 4 when Posture == 0:
                    Posture = 1; Locomotion = 2;
                    break;
                case 5 when Posture == 3:
                    Posture = 2;
                    break;
                case 6 when Locomotion == 2 && Posture == 0:
                    Locomotion = 0;
                    break;
                case 7 when Locomotion != 2:
                    Locomotion = 2;
                    break;
            }
        }
        if (Posture is not (1 or 2) && Locomotion == 2 && (portraitMode != 1 || pendingMood >= 0))
            commands.Add(new("breathe1", "", pendingMood));
        // The receive entry point unconditionally sets +208=1 after consuming
        // the records. It does not dispatch the sit/walk +208 assignments here.
        return commands;
    }
}
