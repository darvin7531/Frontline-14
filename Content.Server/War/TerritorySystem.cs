using Content.Shared.War;
using Robust.Shared.Map;

namespace Content.Server.War;

public sealed partial class TerritorySystem : EntitySystem
{
    public TerritoryState GetState(TerritoryId territory)
    {
        var owners = new HashSet<FactionId>();
        var halls = EntityQueryEnumerator<TownHallComponent, TransformComponent>();
        while (halls.MoveNext(out var uid, out var hall, out var xform))
        {
            if (TerminatingOrDeleted(uid) || hall.Territory != territory || !Contains(territory, xform.Coordinates))
                continue;

            owners.Add(hall.Faction);
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
            if (TerminatingOrDeleted(uid) || hall.Territory != territory || !Contains(territory, xform.Coordinates))
                continue;

            if (faction != default && faction != hall.Faction)
                return false;

            faction = hall.Faction;
        }

        return faction != default;
    }

    public bool Contains(TerritoryId territory, EntityCoordinates coordinates)
    {
        var territories = EntityQueryEnumerator<TerritoryComponent, TransformComponent>();
        while (territories.MoveNext(out var uid, out var definition, out var xform))
        {
            if (TerminatingOrDeleted(uid) || definition.Territory != territory || xform.GridUid != coordinates.EntityId)
                continue;

            return definition.Contains(coordinates.Position);
        }

        return false;
    }
}
