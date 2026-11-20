using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
namespace EnbNext.Client;

public sealed partial class EnbClient
{
    private Net7IncapacitationDetails? _answeredRecovery;
    public Task<bool> ReplyRecoveryAsync(EnbRecoveryRequest request)
    {
        if (_net7 is not {} session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            if (!ReferenceEquals(session, _net7) || session.FixedSessionId != request.Character ||
                world.IsTransitioning || world.PresentationHandoffVersion != request.Handoff ||
                LatestPresentationSnapshot?.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity != request.ShipOwner ||
                !ReferenceEquals(session.Gameplay.IncapacitationDetails, request.Details) ||
                ReferenceEquals(_answeredRecovery, request.Details) || request.Choice is < -1 or > 2 ||
                !Net7WarpStatus.Read(world.LocalShipObjectId is uint id ? world.Objects.FindFirst(id)?.AuxData : null,
                    session.Gameplay.PresentationMode).CanRequestRescue) return Task.FromResult(false);
            _answeredRecovery = request.Details;
            // 00581700: option zero -> 008F; one -> 008E toggle; -1/two close only.
            if (request.Choice == 0) return session.RequestRescueAsync();
            if (request.Choice == 1)
            {
                var ship = world.Objects.FindFirst(world.LocalShipObjectId!.Value);
                bool active = ship?.AuxData?.Properties.FirstOrDefault(p => p.Path == "ShipAuxData.IsRescueBeaconActive")?.Value is byte value && value != 0;
                return session.SendDistressBeaconAsync(!active);
            }
            return Task.FromResult(true);
        });
    }
}
