using Content.Shared.War;
using Robust.Shared.Map;

namespace Content.Server.War;

public sealed partial class FactionSpawnSystem : EntitySystem
{
    [Dependency] private TerritorySystem _territories = default!;

    private bool Usable(EntityUid uid) =>
        !TerminatingOrDeleted(uid) && !EntityManager.IsQueuedForDeletion(uid);

    private bool UsableParent(EntityUid uid)
    {
        while (uid != EntityUid.Invalid)
        {
            if (!Usable(uid) || !TryComp(uid, out TransformComponent? xform))
                return false;
            uid = xform.ParentUid;
        }
        return true;
    }

    // Trusted server lookup: never accept a client-supplied core or coordinates.
    public bool TryGetRespawnBase(FactionId faction, TerritoryId territory, MapId mapId,
        out EntityUid core, out EntityUid pointId, out EntityCoordinates coordinates)
    {
        core = default;
        pointId = default;
        coordinates = default;
        if (string.IsNullOrWhiteSpace(territory.Id) || territory.Id == "Unassigned")
            return false;

        var known = false;
        var markers = EntityQueryEnumerator<TerritoryComponent, TransformComponent>();
        while (markers.MoveNext(out var uid, out var marker, out var xform))
        {
            if (marker.TerritoryId != territory.Id || xform.MapID != mapId)
                continue;
            if (!Usable(uid) || !UsableParent(xform.Coordinates.EntityId))
                return false;
            known = true;
        }
        if (!known)
            return false;

        EntityUid? selected = null;
        var halls = EntityQueryEnumerator<TownHallComponent, TransformComponent>();
        while (halls.MoveNext(out var uid, out var hall, out var xform))
        {
            if (hall.TerritoryId != territory.Id || xform.MapID != mapId || !Usable(uid))
                continue;
            if (selected != null || hall.FactionId != faction.Id ||
                !UsableParent(xform.Coordinates.EntityId) ||
                !_territories.Contains(territory, xform.Coordinates) ||
                !HasComp<FrontlineStockpileComponent>(uid))
                return false;
            selected = uid;
        }

        if (selected is not { } found ||
            !_territories.TryGetOwner(territory, out var owner, mapId) || owner != faction)
            return false;

        var parent = Transform(found).Coordinates.EntityId;
        var points = EntityQueryEnumerator<FactionSpawnPointComponent, TransformComponent>();
        while (points.MoveNext(out var uid, out var point, out var xform))
        {
            if (point.TerritoryId != territory.Id || xform.MapID != mapId || !Usable(uid) ||
                xform.Coordinates.EntityId != parent || !Usable(parent) ||
                !_territories.Contains(territory, xform.Coordinates))
                continue;
            core = found;
            pointId = uid;
            coordinates = xform.Coordinates;
            return true;
        }
        return false;
    }

    public IReadOnlyList<EntityCoordinates> GetAvailableSpawns(FactionId faction, MapId mapId)
    {
        var spawns = new List<EntityCoordinates>();
        var points = EntityQueryEnumerator<FactionSpawnPointComponent, TransformComponent>();
        while (points.MoveNext(out var uid, out var point, out var xform))
        {
            if (TerminatingOrDeleted(uid) || xform.MapID != mapId ||
                !_territories.Contains(new TerritoryId(point.TerritoryId), xform.Coordinates) ||
                !_territories.TryGetOwner(new TerritoryId(point.TerritoryId), out var owner, xform.MapID) ||
                owner != faction)
                continue;

            spawns.Add(xform.Coordinates);
        }

        return spawns;
    }
}
