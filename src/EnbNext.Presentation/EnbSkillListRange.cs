namespace EnbNext.Presentation;

public static class EnbSkillListRange
{
    // 0054A2BA and 0054B184 count ability bands backwards until the last
    // viewport is covered, including a band that crosses its first row.
    public static int Total(IReadOnlyList<bool> abilityBands,int visibleUnits)
    {
        int remaining=visibleUnits,extra=0;
        for(int i=abilityBands.Count-1;i>=0&&remaining>0;i--)
        {
            if(abilityBands[i]){extra++;remaining--;}
            remaining--;
        }
        return abilityBands.Count+extra;
    }
}
