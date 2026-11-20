using System.Text;

namespace EnbNext.Presentation;

public static class EnbStationDialogueText
{
    // Current retail 0064D360 strips slash commands through their next space
    // and collapses the speech punctuation marker "?." to "?".
    // Preserve the wire text separately for speech/animation interpretation.
    public static string Display(string text)
    {
        var result = new StringBuilder(text.Length);
        bool visible = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!visible && c == ' ') visible = true;
            else if (c == '/') visible = false;
            else if (visible) result.Append(c);
            if (c == '?' && i + 1 < text.Length && text[i + 1] == '.') i++;
        }
        return result.ToString();
    }
}
