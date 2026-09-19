using System;
using System.Collections.Generic;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos.Components;
using Content.Shared.Light.Components;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class PersistentWarMapValidatorSystem : EntitySystem
{
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public void Validate(EntityUid map)
    {
        var errors = new List<string>();
        ValidateEnvironment(map, errors);
        ValidateTerritories(map, errors);
        ValidateResourceFields(map, errors);

        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private void ValidateResourceFields(EntityUid map, List<string> errors)
    {
        var mapId = Comp<MapComponent>(map).MapId;
        var fields = new Dictionary<string, FrontlineResourceFieldComponent>();
        var fieldQuery = EntityQueryEnumerator<FrontlineResourceFieldComponent, TransformComponent>();
        while (fieldQuery.MoveNext(out _, out var field, out var xform))
        {
            if (xform.MapID != mapId)
                continue;

            if (string.IsNullOrWhiteSpace(field.FieldId))
                errors.Add("PersistentWar resource field ID must not be empty.");
            else if (!fields.TryAdd(field.FieldId, field))
                errors.Add($"PersistentWar resource field '{field.FieldId}' is duplicated.");
            if (field.MaxReserveNodes <= 0)
                errors.Add($"PersistentWar resource field '{field.FieldId}' must have MaxReserveNodes greater than zero.");
            if (field.MaxActiveNodes <= 0)
                errors.Add($"PersistentWar resource field '{field.FieldId}' must have MaxActiveNodes greater than zero.");
            if (field.ReplacementDelay < TimeSpan.Zero || field.ReplenishmentDelay < TimeSpan.Zero)
                errors.Add($"PersistentWar resource field '{field.FieldId}' delays must not be negative.");
            ValidateResourceNodePrototype(field.FieldId, field.PrimaryNodePrototype, "primary", errors);
        }

        var spawnCounts = new Dictionary<string, int>();
        var spawnQuery = EntityQueryEnumerator<FrontlineResourceSpawnPointComponent, TransformComponent>();
        while (spawnQuery.MoveNext(out _, out var spawn, out var xform))
        {
            if (xform.MapID != mapId)
                continue;

            if (!fields.ContainsKey(spawn.FieldId))
                errors.Add($"PersistentWar resource spawn point references unknown field '{spawn.FieldId}'.");
            else
                spawnCounts[spawn.FieldId] = spawnCounts.GetValueOrDefault(spawn.FieldId) + 1;
        }

        foreach (var id in fields.Keys)
        {
            var count = spawnCounts.GetValueOrDefault(id);
            if (count == 0)
                errors.Add($"PersistentWar resource field '{id}' must have at least one spawn point.");
            else if (fields[id].MaxActiveNodes > count)
                errors.Add($"PersistentWar resource field '{id}' cannot have more active nodes than spawn points.");
        }
    }

    private void ValidateResourceNodePrototype(string fieldId, EntProtoId prototype, string kind, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(prototype.Id) ||
            !_prototypes.TryIndex<EntityPrototype>(prototype, out var entity) ||
            !entity.TryGetComponent<FrontlineResourceNodeComponent>(out var node, EntityManager.ComponentFactory))
        {
            errors.Add($"PersistentWar resource field '{fieldId}' {kind} node prototype '{prototype}' is invalid.");
            return;
        }

        if (node.MaxYield <= 0 || node.HarvestAmount <= 0 || node.HarvestAmount > node.MaxYield)
            errors.Add($"PersistentWar resource field '{fieldId}' {kind} node must have positive yield and harvest amount no greater than yield.");
        if (node.ExtractionTime < TimeSpan.Zero)
            errors.Add($"PersistentWar resource field '{fieldId}' {kind} node extraction time must not be negative.");
        if (!_prototypes.HasIndex<StackPrototype>(node.Output))
            errors.Add($"PersistentWar resource field '{fieldId}' {kind} node output '{node.Output}' is invalid.");

        foreach (var bonus in node.BonusDrops)
        {
            if (!float.IsFinite(bonus.Chance) || bonus.Chance is < 0f or > 1f || bonus.MinAmount <= 0 ||
                bonus.MaxAmount < bonus.MinAmount || bonus.MaxAmount == int.MaxValue ||
                !_prototypes.HasIndex<StackPrototype>(bonus.Output))
                errors.Add($"PersistentWar resource field '{fieldId}' {kind} node has an invalid bonus drop '{bonus.Output}'.");
        }
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
            var objectives = CountContained(halls, id) + CountContained(ruins, id);
            if (objectives != 1)
                errors.Add($"PersistentWar map territory '{id}' must include exactly one objective within its bounds (found {objectives}).");

            if (CountContained(spawns, id) == 0)
                errors.Add($"PersistentWar map territory '{id}' must include a faction spawn point within its bounds.");
        }

        if (halls.Count != 2 || ruins.Count != 3)
            errors.Add($"PersistentWar map must include exactly two town halls and three ruins (found {halls.Count} halls and {ruins.Count} ruins).");

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

    private int CountContained<T>(
        List<Entity<T, TransformComponent>> entities,
        string id)
        where T : IComponent
    {
        var count = 0;
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
                count++;
        }

        return count;
    }

    private void ValidateStartingHall(
        Dictionary<string, Entity<TerritoryComponent, TransformComponent>> territories,
        List<Entity<TownHallComponent, TransformComponent>> halls,
        string faction,
        List<string> errors)
    {
        var count = 0;
        foreach (var hall in halls)
        {
            if (hall.Comp1.FactionId == faction && territories.TryGetValue(hall.Comp1.TerritoryId, out var territory) &&
                _territories.Contains(new TerritoryId(territory.Comp1.TerritoryId), hall.Comp2.Coordinates))
                count++;
        }

        if (count != 1)
            errors.Add($"PersistentWar map must include exactly one starting town hall for {faction} (found {count}).");
    }
}