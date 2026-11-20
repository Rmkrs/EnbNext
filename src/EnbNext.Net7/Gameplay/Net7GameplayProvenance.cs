namespace EnbNext.Net7.Gameplay;

/// <summary>
/// Executable provenance ledger for gameplay behavior implemented by the
/// active direct-Net7 path. Values are research dossier IDs.
/// </summary>
public static class Net7GameplayProvenance
{
    private static readonly IReadOnlyDictionary<ushort, string[]> Owners = new Dictionary<ushort, string[]>
    {
        [0x0009] = ["COMBAT-04"],
        [0x000B] = ["COMBAT-04"],
        [0x000F] = ["COMBAT-04"],
        [0x0012] = ["MOVE-01"],
        [0x0013] = ["MOVE-02"],
        [0x0014] = ["MOVE-03", "MOVE-04"],
        [0x0015] = ["COMBAT-10"],
        [0x0017] = ["COMBAT-01"],
        [0x0018] = ["COMBAT-02"],
        [0x0019] = ["COMBAT-03"],
        [0x001A] = ["FULL-06"],
        [0x001D] = ["SOCIAL-03", "FULL-06"],
        [0x001E] = ["SOCIAL-05"],
        [0x001F] = ["ECON-02"],
        [0x0027] = ["INV-04", "INV-05", "ECON-01", "ECON-02"],
        [0x0028] = ["INV-02"],
        [0x0029] = ["INV-01"],
        [0x002C] = ["MOVE-07", "MOVE-08", "MOVE-11", "ECON-02", "SOCIAL-05"],
        [0x002D] = ["SOCIAL-05"],
        [0x002E] = ["SOCIAL-05"],
        [0x0033] = ["SOCIAL-01"],
        [0x003C] = ["ENTRY-09"],
        [0x004E] = ["MOVE-09", "MOVE-10"],
        [0x0051] = ["INV-05"],
        [0x0053] = ["SOCIAL-07"],
        [0x0055] = ["FULL-06"],
        [0x0057] = ["COMBAT-08"],
        [0x0058] = ["COMBAT-09"],
        [0x0059] = ["COMBAT-10"],
        [0x005A] = ["FULL-06"],
        [0x005D] = ["INV-03"],
        [0x005E] = ["SOCIAL-02"],
        [0x005F] = ["SOCIAL-04"],
        [0x0064] = ["COMBAT-10"],
        [0x0065] = ["INV-05", "MISSION-01", "FULL-06"],
        [0x0068] = ["FULL-06"],
        [0x0079] = ["MFG-01"],
        [0x007A] = ["MFG-02"],
        [0x007B] = ["MFG-03"],
        [0x007C] = ["MFG-04"],
        [0x007E] = ["MFG-05"],
        [0x007F] = ["MFG-06"],
        [0x0080] = ["MFG-07"],
        [0x0082] = ["FULL-06"],
        [0x0084] = ["FULL-06"],
        [0x0086] = ["MISSION-02"],
        [0x0087] = ["MISSION-03"],
        [0x0088] = ["FULL-06"],
        [0x008C] = ["INV-05"],
        [0x008D] = ["COMBAT-10"],
        [0x008E] = ["COMBAT-10"],
        [0x008F] = ["COMBAT-10"],
        [0x0090] = ["COMBAT-10"],
        [0x0091] = ["COMBAT-10"],
        [0x0098] = ["FULL-06"],
        [0x009B] = ["MOVE-05"],
        [0x009D] = ["FULL-06"],
        [0x009F] = ["FULL-03", "FULL-06"],
        [0x00A1] = ["FULL-06"],
        [0x00A3] = ["SOCIAL-02", "SOCIAL-04", "SOCIAL-07", "ENTRY-09"],
        [0x00A4] = ["SOCIAL-02", "SOCIAL-07"],
        [0x00A5] = ["SOCIAL-02", "SOCIAL-04", "SOCIAL-07"],
        [0x00A6] = ["SOCIAL-02", "SOCIAL-04", "SOCIAL-07"],
        [0x00B9] = ["WORLD-13", "FULL-03"],
        [0x00BC] = ["SOCIAL-05"],
        [0x00BD] = ["SOCIAL-05"],
        [0x00C0] = ["FULL-06"],
        [0x00C5] = ["SOCIAL-06"],
        [0x00C8] = ["SOCIAL-06"],
        [0x00C9] = ["SOCIAL-06"],
        [0x00CC] = ["SOCIAL-06"],
        [0x00CD] = ["SOCIAL-06"],
        [0x00D0] = ["SOCIAL-06"],
        [0x00D2] = ["SOCIAL-06"],
        [0x00D3] = ["SOCIAL-06"],
        [0x00D4] = ["SOCIAL-06"],
        [0x00D8] = ["SOCIAL-06"],
        [0x00E8] = ["FULL-06"],
        [0x00EC] = ["FULL-06"],
        [0x00ED] = ["FULL-06"],
        [0x1004] = ["MOVE-04", "WORLD-14"],
        [0x2012] = ["COMBAT-05"],
        [0x2013] = ["COMBAT-06"],
        [0x2014] = ["COMBAT-07"],
        [0x2025] = ["ASSET-01", "WORLD-12"],
        [0x202A] = ["MOVE-06"],
    };

    public static IReadOnlyList<string> GetOwners(ushort opcode)
        => Owners.TryGetValue(opcode, out string[]? owners) ? owners : Array.Empty<string>();

    public static bool IsImplemented(ushort opcode) => Owners.ContainsKey(opcode);

    public static void RequireImplemented(ushort opcode)
    {
        if (!Owners.ContainsKey(opcode))
            throw new InvalidOperationException($"Gameplay opcode 0x{opcode:X4} has no STATIC-CLOSED ENB-06 provenance owner.");
    }
}
