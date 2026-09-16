using System;
using System.Collections.Generic;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos.Components;
using Content.Shared.Light.Components;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.War;

public sealed partial class PersistentWarMapValidatorSystem : EntitySystem
{
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private TerritorySystem _territories = default!;

    public void Validate(EntityUid map)
    {
        var errors = new List<string>();
        ValidateEnvironment(map, errors);
        ValidateTerritories(map, errors);

        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private void ValidateEnvironment(EntityUid map, List<string> errors)
    {
        if (!TryComp(map, out MapAtmosphereComponent? atmosphere))
            errors.Add("PersistentWar map must include MapAtmosphere.");
        else if (atmosphere.Space)
            errors.Add("PersistentWar map must set MapAtmosphere.space to false.");
        else if (!_atmosphere.IsMixtureProbablySafe(_atmosphere.GetTileMixture(null, (map, atmosphere), default)))
            errors.Add("PersistentWar map MapAtmosphere must provide safe pressure and temperature.");

        if (!HasComp<MapLightComponent>(map))
            errors.Add("PersistentWar map must include MapLight.");

        if (!TryComp(map, out LightCycleComponent? cycle))
            errors.Add("PersistentWar map must include LightCycle.");
        else if (!cycle.Enabled || cycle.Duration <= TimeSpan.Zero)
            errors.Add("PersistentWar map LightCycle must be enabled with a positive duration.");
    }

    private void ValidateTerritories(EntityUid map, List<string> errors)
    {
        var mapId = Comp<MapComponent>(map).MapId;
        var territories = new Dictionary<string, Entity<TerritoryComponent, TransformComponent>>();
        var markers = EntityQueryEnumerator<TerritoryComponent, TransformComponent>();
        while (markers.MoveNext(out var uid, out var territory, out var xform))
        {
            if (TerminatingOrDeleted(uid) || xform.MapID != mapId)
                continue;

            if (territory.TerritoryId == "Unassigned")
            {
                errors.Add("PersistentWar map territory markers must not use Unassigned.");
                continue;
            }

            if (!territories.TryAdd(territory.TerritoryId, (uid, territory, xform)))
                errors.Add($"PersistentWar map territory '{territory.TerritoryId}' is duplicated.");
        }

        if (territories.Count != 5)
            errors.Add($"PersistentWar map must define exactly five territories (found {territories.Count}).");

        var halls = GetMapEntities<TownHallComponent>(mapId);
        var ruins = GetMapEntities<TownHallRuinComponent>(mapId);
        var spawns = GetMapEntities<FactionSpawnPointComponent>(mapId);
        foreach (var id in territories.Keys)
        {
            if (!Contains(halls, id) && !Contains(ruins, id))
                errors.Add($"PersistentWar map territory '{id}' must include a town hall or ruin within its bounds.");

            if (!Contains(spawns, id))
                errors.Add($"PersistentWar map territory '{id}' must include a faction spawn point within its bounds.");
        }

        if (territories.Count == 0)
        {
            errors.Add("PersistentWar map must include a town hall or ruin for each territory.");
            errors.Add("PersistentWar map must include a faction spawn point for each territory.");
        }

        ValidateStartingHall(territories, halls, "FrontlineFactionOne", errors);
        ValidateStartingHall(territories, halls, "FrontlineFactionTwo", errors);
    }

    private List<Entity<T, TransformComponent>> GetMapEntities<T>(MapId mapId) where T : IComponent
    {
        var result = new List<Entity<T, TransformComponent>>();
        var entities = EntityQueryEnumerator<T, TransformComponent>();
        while (entities.MoveNext(out var uid, out var component, out var xform))
        {
            if (!TerminatingOrDeleted(uid) && xform.MapID == mapId)
                result.Add((uid, component, xform));
        }

        return result;
    }

    private bool Contains<T>(
        List<Entity<T, TransformComponent>> entities,
        string id)
        where T : IComponent
    {
        foreach (var entity in entities)
        {
            var entityId = entity.Comp1 switch
            {
                TownHallComponent hall => hall.TerritoryId,
                TownHallRuinComponent ruin => ruin.TerritoryId,
                FactionSpawnPointComponent spawn => spawn.TerritoryId,
                _ => string.Empty,
            };

            if (entityId == id && _territories.Contains(new TerritoryId(id), entity.Comp2.Coordinates))
                return true;
        }

        return false;
    }

    private void ValidateStartingHall(
        Dictionary<string, Entity<TerritoryComponent, TransformComponent>> territories,
        List<Entity<TownHallComponent, TransformComponent>> halls,
        string faction,
        List<string> errors)
    {
        foreach (var hall in halls)
        {
            if (hall.Comp1.FactionId == faction && territories.TryGetValue(hall.Comp1.TerritoryId, out var territory) &&
                _territories.Contains(new TerritoryId(territory.Comp1.TerritoryId), hall.Comp2.Coordinates))
                return;
        }

        errors.Add($"PersistentWar map must include a starting town hall for {faction}.");
    }
}