namespace EnbNext.Assets;

/// <summary>
/// EnbNext-authored character-creation ratings for professions that were
/// disabled in retail and therefore have no authoritative retail profile.
/// The seven values are Defense, Offense, Energy, Speed, Cargo, Scanning,
/// and Stealth, in the order consumed by fa_fe_class00.
/// </summary>
public static class EnbNextCharacterCreationAttributes
{
    private static readonly int[] TerranScout = [2, 2, 3, 5, 4, 5, 2];
    private static readonly int[] JenquaiSeeker = [2, 2, 5, 3, 3, 3, 4];
    private static readonly int[] ProgenPrivateer = [4, 4, 4, 1, 3, 3, 2];

    public static bool TryGetRatings(uint race, uint profession, out ReadOnlyMemory<int> ratings)
    {
        int[]? values = (race, profession) switch
        {
            (0, 2) => TerranScout,
            (1, 1) => JenquaiSeeker,
            (2, 1) => ProgenPrivateer,
            _ => null,
        };

        ratings = values;
        return values is not null;
    }
}
