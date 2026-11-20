namespace EnbNext.Client.Models;

public sealed record SavedGameCharacter(int Slot, string Name, string? Race, string? Profession, ulong? Level = null);
public sealed record SavedGameAccount(Guid Id, string Server, string Login, SavedGameCharacter[] Characters);
