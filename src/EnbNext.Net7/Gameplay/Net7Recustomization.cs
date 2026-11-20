using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Gameplay;

/// <summary>Current retail 008493F0/008494C0 and 00849AC0/00849B90 cost groups.</summary>
public static class Net7Recustomization
{
    public static int AvatarCost(Net7AvatarData before, Net7AvatarData after, IReadOnlyList<uint> costs)
    {
        if (costs.Count != 14) throw new ArgumentException("Fourteen avatar costs required.", nameof(costs));
        bool NearChanged(float[] a, float[] b) => a.Zip(b).Any(p => Math.Abs(p.First - p.Second) > (float)0.0001);
        bool Cloth(float[] a, uint am, float[] b, uint bm) => !(am == bm && am != uint.MaxValue) &&
            (am != bm || !a.SequenceEqual(b));
        bool[] changed = [before.HairNum != after.HairNum, before.BeardNum != after.BeardNum,
            NearChanged(before.HairColor, after.HairColor), NearChanged(before.SkinColor, after.SkinColor),
            NearChanged(before.EyeColor, after.EyeColor),
            before.TattooTextureNum != after.TattooTextureNum || before.TattooTextureNum != 0 && NearChanged(before.TattooOffset, after.TattooOffset),
            before.GoggleNum != after.GoggleNum, before.EarNum != after.EarNum, before.BodyType != after.BodyType,
            Cloth(before.ShirtPrimaryColor,before.ShirtPrimaryMetal,after.ShirtPrimaryColor,after.ShirtPrimaryMetal),
            Cloth(before.ShirtSecondaryColor,before.ShirtSecondaryMetal,after.ShirtSecondaryColor,after.ShirtSecondaryMetal),
            before.PantsType != after.PantsType,
            Cloth(before.PantsPrimaryColor,before.PantsPrimaryMetal,after.PantsPrimaryColor,after.PantsPrimaryMetal),
            Cloth(before.PantsSecondaryColor,before.PantsSecondaryMetal,after.PantsSecondaryColor,after.PantsSecondaryMetal)];
        return Sum(changed, costs);
    }

    public static int ShipCost(Net7ShipCustomization before, Net7ShipCustomization after, IReadOnlyList<uint> costs)
    {
        if (costs.Count != 12 || before.PartAttributes.Count != 8 || after.PartAttributes.Count != 8)
            throw new ArgumentException("Twelve ship costs and eight material entries required.");
        // Cost order differs from serializer order: hull, wings, profession, engines.
        int[] order = [0,1,4,5,2,3,6,7];
        var changed = new List<bool> { before.HullStyle != after.HullStyle, before.WingStyle != after.WingStyle };
        changed.AddRange(order.Select(i => before.PartAttributes[i] != after.PartAttributes[i]));
        changed.Add(before.ShipName != after.ShipName || before.ShipNameColor != after.ShipNameColor);
        changed.Add(before.SelectedDecal != after.SelectedDecal);
        return Sum(changed, costs);
    }

    private static int Sum(IReadOnlyList<bool> changed, IReadOnlyList<uint> costs)
    {
        int sum = 0;
        for (int i=0;i<changed.Count;i++) if (changed[i]) sum = unchecked(sum + (int)costs[i]);
        return sum;
    }

    public static byte[] EncodeShip(Net7ShipCustomization ship)
    {
        if (ship.PartAttributes.Count != 8) throw new ArgumentException("Eight ship material entries required.");
        if (ship.ShipName.Length > 25 || ship.ShipName.Any(c => c == 0 || c > 255))
            throw new ArgumentException("Ship name must fit 25 Latin-1 characters.");
        byte[] bytes = new byte[Net7PresentationWire.ShipCustomizationSize];
        void U(int at,uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
        void V(int at,Vector3 v) { U(at,BitConverter.SingleToUInt32Bits(v.X)); U(at+4,BitConverter.SingleToUInt32Bits(v.Y)); U(at+8,BitConverter.SingleToUInt32Bits(v.Z)); }
        U(0,ship.Profession); U(4,ship.TechLevel); U(8,ship.HullStyle); U(12,ship.WingStyle); U(16,ship.SelectedDecal);
        Encoding.Latin1.GetBytes(ship.ShipName).CopyTo(bytes,20); V(46,ship.ShipNameColor);
        for (int i=0;i<8;i++) { int at=58+i*17; var p=ship.PartAttributes[i]; V(at,p.Color); bytes[at+12]=p.ByteValue; U(at+13,p.DwordValue); }
        return bytes;
    }
}
