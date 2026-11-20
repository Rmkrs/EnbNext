namespace EnbNext.Assets;

public sealed record EnbRetailCameraOptions(bool Ship, bool Warp, bool Loot, bool Gate, bool Land, bool Dock, bool Bridge)
{
    public (string Name, bool Enabled)[] Values => [("Camera_Ship",Ship),("Camera_Warp",Warp),
        ("Camera_Loot",Loot),("Camera_Gate",Gate),("Camera_Land",Land),("Camera_Dock",Dock),("Camera_Bridge",Bridge)];
}
