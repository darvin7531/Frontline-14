using System.Linq;
using Content.Shared.War;
using Robust.Shared.Map;

namespace Content.Server.War;

public sealed partial class TerritorySystem : EntitySystem
{
    public int CountOwned(FactionId faction)
    {
        var territoryIds = new HashSet<TerritoryId>();
        var territories = EntityQueryEnumerator<TerritoryComponent>();
        while (territories.MoveNext(out var uid, out var territory))
        {
            if (!TerminatingOrDeleted(uid))
                territoryIds.Add(new TerritoryId(territory.TerritoryId));
        }

        return territoryIds.Count(territory => TryGetOwner(territory, out var owner) && owner == faction);
    }

    public TerritoryState GetState(TerritoryId territory)
    {
        var owners = new HashSet<FactionId>();
        var halls = EntityQueryEnumerator<TownHallComponent, TransformComponent>();
        while (halls.MoveNext(out var uid, out var hall, out var xform))
        {
            if (TerminatingOrDeleted(uid) || hall.TerritoryId != territory.Id || !Contains(territory, xform.Coordinates))
                continue;

            owners.Add(new FactionId(hall.FactionId));
            if (owners.Count > 1)
                return TerritoryState.Contested;
        }

        return owners.Count == 1 ? TerritoryState.Owned : TerritoryState.Neutral;
    }

    public bool TryGetOwner(TerritoryId territory, out FactionId faction)
    {
        faction = default;
        var halls = EntityQueryEnumerator<TownHallComponent, TransformComponent>();
        while (halls.MoveNext(out var uid, out var hall, out var xform))
        {
            if (TerminatingOrDeleted(uid) || hall.TerritoryId != territory.Id || !Contains(territory, xform.Coordinates))
                continue;

            var owner = new FactionId(hall.FactionId);
            if (faction != default && faction != owner)
                return false;

            faction = owner;
        }

        return faction != default;
    }

    public bool Contains(TerritoryId territory, EntityCoordinates coordinates)
    {
        var territories = EntityQueryEnumerator<TerritoryComponent, TransformComponent>();
        while (territories.MoveNext(out var uid, out var definition, out var xform))
        {
            if (TerminatingOrDeleted(uid) || definition.TerritoryId != territory.Id || xform.GridUid != coordinates.EntityId)
                continue;

            return definition.Contains(coordinates.Position);
        }

        return false;
    }
}
