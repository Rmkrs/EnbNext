using System.Globalization;
using EnbNext.Presentation;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>68CCC0 common slot tooltip with 689F90/68A010 missing-value defaults.</summary>
public static class EnbRetailItemTooltip
{
    public static string EmptyDescription(uint mountMask) => (mountMask switch
    {
        0=>"Unused Slot",4=>"Shields Slot",0x40=>"Engine Slot",0x80=>"Reactor Slot",
        uint.MaxValue=>"Empty Slot",
        _ when (mountMask&0x1c022)!=0=>"Weapon Slot",
        _ when (mountMask&0x20809)!=0=>"Device Slot",
        _=>"Empty Slot"
    })+"\n";

    // 0068B980, used by inventory panel types12/13 (wire inventories13/12).
    public static string ManufacturingDescription(EnbSceneItemSnapshot? item,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if(item is null)return "Empty Slot\n";
        string text=item.Name+"\n";
        if((item.Structure??0)!=-1)
        {
            using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
            text+=string.Format(CultureInfo.InvariantCulture,"Quality {0:F0}%\n",
                (double)((R)(item.Quality??0)*(R)100f));
        }
        return text;
    }

    public static string Description(EnbSceneItemSnapshot item,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        string text=item.Name+"\n";
        float structure=item.Structure??0,quality=item.Quality??0;
        if(structure!=-1)
        {
            using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
            text+=string.Format(CultureInfo.InvariantCulture,"Structure {0:F0}%\nQuality {1:F0}%\n",
                (double)((R)structure*(R)100f),(double)((R)quality*(R)100f));
        }
        if(item.Price is >0 and not ulong.MaxValue and var price)
            text+="Price "+price.ToString("N0",CultureInfo.InvariantCulture)+"\n";
        if(item.ItemType==13 && item.AverageCost is {} cost && cost!=0)
            text+="Avg. Cost "+((double)cost).ToString("F1",CultureInfo.InvariantCulture)+"\n";
        return text;
    }
}
