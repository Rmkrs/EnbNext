using System.Net;

namespace EnbNext.Client.Models;

public sealed record SectorRouteResult(
    CharacterSummary Character,
    uint FixedSessionId,
    uint RetainedMasterResponseDword,
    IPEndPoint GameplaySectorEndPoint);
