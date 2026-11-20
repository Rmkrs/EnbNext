using EnbNext.Net7.Gameplay;
namespace EnbNext.Presentation;

public sealed record EnbRecoveryRequest(uint Character, ulong Handoff, ulong ShipOwner,
    Net7IncapacitationDetails Details, int Choice);
