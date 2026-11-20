using System.Collections.Immutable;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Next multibox "Shared harvesting" as the leader sees it: what a follower that can harvest knows of an asteroid (its
/// contents, whether its Harvest button is there) and its open harvest window. Supplied by the host from the follower's
/// published state; the commands go back to that follower.
/// </summary>
internal sealed record EnbRemoteHarvest(string Pilot, uint Target, ImmutableArray<string> Contents, uint Verb, bool Executable, bool Open,
    ImmutableArray<EnbSceneItemSnapshot> Items, int TechLevel);

internal sealed partial class EnbWorldUi
{
    internal const uint HarvestVerb = 0x00090000;
    /// <summary>What the followers that share harvesting know of this target (one per follower).</summary>
    internal Func<uint, IReadOnlyList<EnbRemoteHarvest>>? RemoteHarvest { get; set; }
    /// <summary>A command for a follower (by name): "harvest" (its Harvest button), "harvestItem" (a slot), "harvestClose".</summary>
    internal Action<string, uint, int, string>? RemoteHarvestCommand
    {
        get => _remoteHarvestCommand;
        set { _remoteHarvestCommand = value; Inventory.RemoteHarvestCommand = value; }
    }
    private Action<string, uint, int, string>? _remoteHarvestCommand;
}
