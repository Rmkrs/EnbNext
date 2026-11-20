using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>006F4610 fixed-child visibility; slot order is the 006FCB70 factory order.</summary>
public static class EnbShipManeuverJetRuntime
{
    public const float BankThreshold = .15707964f; // 00B007D4 / 00B007D8

    public static bool IsActive(IEnbRenderObject ship, int slot)
    {
        if (slot is < 0 or > 5 || ship.ObjectType is not (0 or 1) ||
            ship.ShipPresentation?.IsOrganic == true) return false;
        // Authoritative reverse/braking state overrides both bank branches.
        if (ship.ShipPresentation?.EngineThrustState == 2) return slot >= 4;
        if (ship.ShipVisualBankRadians is not float bank || !float.IsFinite(bank)) return false;
        return bank > BankThreshold ? slot is 0 or 3 :
            bank < -BankThreshold && slot is 1 or 2;
    }
}
