using Content.Shared.War;
using Robust.Shared.Map;

namespace Content.Server.War;

public sealed partial class FactionSpawnSystem : EntitySystem
{
    public IReadOnlyList<EntityCoordinates> GetAvailableSpawns(FactionId faction)
    {
        // ponytail: no map-backed spawn providers exist yet; add Town Hall/clan-base providers with the owned map.
        // Town Hall and clan-base spawn providers will populate this when those map entities exist.
        return Array.Empty<EntityCoordinates>();
    }
}
