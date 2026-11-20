namespace EnbNext.Client.Models;

public sealed record SavedGroupMember(Guid Id, Guid AccountId, string Character);
public sealed record SavedLoginGroup(Guid Id, string Name, string Server, SavedGroupMember[] Members);

public static class LoginGroupRules
{
    public static string? Validate(SavedLoginGroup group)
    {
        if (group.Id == Guid.Empty || string.IsNullOrWhiteSpace(group.Name) || group.Name.Trim().Length > 40)
            return "Enter a group name of 1 to 40 characters.";
        if (string.IsNullOrWhiteSpace(group.Server)) return "Choose a server.";
        if (group.Members is null || group.Members.Length is < 2 or > 6) return "Choose between two and six characters.";
        if (group.Members.Any(m => m is null || m.Id == Guid.Empty || m.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(m.Character)))
            return "Every member needs an account and character.";
        if (group.Members.Select(m => m.AccountId).Distinct().Count() != group.Members.Length)
            return "Only one character per account can join a group.";
        if (group.Members.Select(m => m.Id).Distinct().Count() != group.Members.Length) return "Group member identities must be distinct.";
        return null;
    }
}
