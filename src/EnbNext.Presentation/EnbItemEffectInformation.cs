using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Presentation;

// Unmet: 00538350 appends _UNUSABLE (FA_RED) when the row's requirement predicate fails (INV-D04).
public readonly record struct EnbItemSpecial(string Text, string Tooltip, bool Unmet = false);

// 00527F40/0083E5F0 apply instance summaries to the two template vectors.
// 00833D00/00833D40 format their independent name and description float lists.
internal static class EnbItemEffectInformation
{
    internal static void Append(ImmutableArray<EnbItemSpecial>.Builder rows, Net7ItemTemplateVector vector, string instance)
    {
        var summaries=Split(instance,'#');
        for(int i=0;i<vector.Entries.Count;i++)
        {
            var e=vector.Entries[i];
            var parts=i<summaries.Length?Split(summaries[i],'^'):[];
            var names=parts.Length==2?Split(parts[0],','):[];
            var descriptions=parts.Length==2?Split(parts[1],','):[];
            rows.Add(new("Effect: "+Format(e.StringB.EffectiveText,e.ListA,names),
                Format(e.StringC.EffectiveText,e.ListB,descriptions)));
        }
    }
    // 004FC2F0 preserves leading/interior empty fields, but omits a trailing empty field.
    private static string[] Split(string text,char separator)
    {
        if(text.Length==0)return [];
        var parts=text.Split(separator);
        return text[^1]==separator?parts[..^1]:parts;
    }
    // 008335D0 repeatedly takes the leftmost "%value" and the next '%' (00833880), formats
    // the following value with sprintf("%"+spec) into a 32-byte stack buffer, and replaces that match.
    private static string Format(string template,IReadOnlyList<uint> values,string[] overrides)
    {
        string text=template;
        for(int index=0;;index++)
        {
            int start=text.IndexOf("%value",StringComparison.Ordinal);
            if(start<0)return text;
            int end=text.IndexOf('%',start+1);
            // Safety rule: unterminated at 0 ends the loop; beyond 0, 004C39B0 throws out_of_range.
            if(end<0)return text;
            float value=index<values.Count?BitConverter.UInt32BitsToSingle(values[index]):0;
            if(index<values.Count&&index<overrides.Length)value=ParseValue(overrides[index]);
            // Safety rule: a conversion with no defined native result keeps the text decoded so far.
            string? formatted=EnbRetailCrtFormat.Format("%"+text[(start+6)..end],value,32);
            if(formatted is null)return text;
            int nul=formatted.IndexOf('\0');
            text=text[..start]+(nul<0?formatted:formatted[..nul])+text[(end+1)..];
        }
    }
    private static float ParseValue(string text)
    {
        // Retail atof accepts a numeric prefix and returns zero for an empty/non-number field.
        var s=text.AsSpan().TrimStart();int end=0;
        if(end<s.Length&&s[end] is '+' or '-')end++;
        while(end<s.Length&&char.IsAsciiDigit(s[end]))end++;
        if(end<s.Length&&s[end]=='.'){end++;while(end<s.Length&&char.IsAsciiDigit(s[end]))end++;}
        int numeric=end;
        if(end<s.Length&&s[end] is 'e' or 'E')
        {
            end++;if(end<s.Length&&s[end] is '+' or '-')end++;
            int digits=end;while(end<s.Length&&char.IsAsciiDigit(s[end]))end++;
            if(end==digits)end=numeric;
        }
        return float.TryParse(s[..end],NumberStyles.Float,CultureInfo.InvariantCulture,out var value)?value:0;
    }
}
