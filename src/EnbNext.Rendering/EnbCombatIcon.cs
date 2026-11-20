using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbCombatIcon(int Slot, Vector3 Anchor, EnbRetailWeaponFeedback State,
    bool Invalid = false, bool WrongTarget = false, float? Powerup = null, bool NoAmmo = false, bool CannotUse = false);
