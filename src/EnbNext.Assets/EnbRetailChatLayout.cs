using System.Numerics;
namespace EnbNext.Assets;

public sealed record EnbRetailChatLayout(string Prefix, double BaseHeight, double BaseSplit, double Width, int SizeFrames, int SplitFrames)
{
    public EnbW3dHierarchy Hierarchy { get; private init; } = null!;
    public EnbW3dHierarchyAnimation SizeAnimation { get; private init; } = null!;
    public EnbW3dHierarchyAnimation SplitAnimation { get; private init; } = null!;
    public static EnbRetailChatLayout Load(EnbRetailAssetBundle assets, int mode, double headerHeight)
    {
        string modelName = mode switch { 0 => "tsi05f", 1 => "tsi05e", 2 => "tsi05d", _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        EnbW3dModel Read(string name)
        {
            if (assets.RetailMixResources is not { } resources || !resources.TryReadResource(name + ".w3d", out var bytes, out _) || !EnbW3dModel.TryDecode(bytes, out var model))
                throw new InvalidDataException("Missing retail chat layout: " + name);
            return model;
        }
        var hierarchy = Read(modelName).Hierarchies.Single();
        var size = Read(modelName + "-1").Animations.Single();
        var split = Read(modelName + "-2").Animations.Single();
        Vector3 Position(string name, bool splitFrame = false)
        {
            var pivot = hierarchy.Pivots.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Vector3 position = default;
            while (true)
            {
                position += pivot.Translation;
                foreach (var channel in (splitFrame ? split : size).Channels.Where(c => c.PivotIndex == pivot.Ordinal))
                {
                    if (channel.Flags == 0) position.X += channel.Data[0];
                    if (channel.Flags == 1) position.Y += channel.Data[0];
                    if (channel.Flags == 2) position.Z += channel.Data[0];
                }
                if (pivot.IsRoot) break;
                pivot = hierarchy.Pivots[(int)pivot.ParentIndex];
            }
            return position;
        }
        double top = mode == 1 ? Position("VP1_TL").Y : Position("VP0_TL").Y;
        double baseHeight = headerHeight + top - Position("CHAT_VP_TL").Y;
        double baseSplit = mode == 1 ? top - Position("VP1_BR", true).Y : 0;
        double width = mode == 2 ? Position("VP1_BR").X - Position("VP0_TL").X : 0;
        return new("ChatWinSplit_" + (mode == 0 ? "None" : mode == 1 ? "Horz" : "Vert"), baseHeight, baseSplit, width,
            (int)size.NumFrames - 1, (int)split.NumFrames - 1)
        { Hierarchy = hierarchy, SizeAnimation = size, SplitAnimation = split };
    }
    public double Height(int frame) => BaseHeight + Math.Clamp(frame, 0, SizeFrames);
    public int HeightFrame(double height) => Math.Clamp((int)Math.Round(height - BaseHeight), 0, SizeFrames);
    public double Split(int frame, bool vertical) => vertical ? Math.Clamp(frame, 0, SplitFrames) / Width * 100 : BaseSplit + Math.Clamp(frame, 0, SplitFrames);
    public int SplitFrame(double split, bool vertical) => Math.Clamp((int)Math.Round(vertical ? split * Width / 100 : split - BaseSplit), 0, SplitFrames);
}
