using Content.Shared.War;
using Robust.Shared.Map;

namespace Content.Server.War;

public sealed partial class FactionSpawnSystem : EntitySystem
{
    [Dependency] private TerritorySystem _territories = default!;

    public IReadOnlyList<EntityCoordinates> GetAvailableSpawns(FactionId faction)
    {
        var spawns = new List<EntityCoordinates>();
        var points = EntityQueryEnumerator<FactionSpawnPointComponent, TransformComponent>();
        while (points.MoveNext(out var uid, out var point, out var xform))
        {
            if (TerminatingOrDeleted(uid) ||
                !_territories.Contains(point.Territory, xform.Coordinates) ||
                !_territories.TryGetOwner(point.Territory, out var owner) ||
                owner != faction)
                continue;

            spawns.Add(xform.Coordinates);
        }

        return spawns;
    }
}
