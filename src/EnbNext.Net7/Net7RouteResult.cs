using System.Net;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7;

public sealed record Net7RouteResult(
    Net7AvatarSlot Character,
    uint FixedSessionId,
    uint RetainedMasterResponseDword,
    IPEndPoint GameplaySectorEndPoint,
    Net7SectorJoinClientData SectorJoinData);
