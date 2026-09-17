using System;
using Content.Server.Stack;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class TownHallSystem : EntitySystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private WarFactionSystem _factions = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<TownHallComponent, EntityTerminatingEvent>(OnTownHallTerminating);
        SubscribeLocalEvent<TownHallRuinComponent, AfterInteractEvent>(OnRuinInteract);
        SubscribeLocalEvent<TownHallRuinComponent, TownHallRepairDoAfterEvent>(OnRepairComplete);
    }

    private void OnTownHallTerminating(Entity<TownHallComponent> hall, ref EntityTerminatingEvent args)
    {
        if (hall.Comp.TerritoryId == "Unassigned")
            return;

        var coordinates = Transform(hall).Coordinates;
        if (TerminatingOrDeleted(coordinates.EntityId))
            return;

        var ruin = Spawn("TownHallRuin", coordinates);
        Comp<TownHallRuinComponent>(ruin).TerritoryId = hall.Comp.TerritoryId;
    }

    public bool ForceCapture(TerritoryId territory, FactionId faction, MapId? mapId = null)
    {
        if (!_prototypes.TryIndex<FrontlineFactionPrototype>(faction.Id, out var factionPrototype))
            return false;

        var objectives = new List<EntityUid>();
        var coordinates = EntityCoordinates.Invalid;
        var halls = EntityQueryEnumerator<TownHallComponent, TransformComponent>();
        while (halls.MoveNext(out var uid, out var hall, out var transform))
        {
            if (hall.TerritoryId != territory.Id ||
                mapId is { } hallMap && transform.MapID != hallMap ||
                !_territories.Contains(territory, transform.Coordinates))
                continue;

            objectives.Add(uid);
            if (!coordinates.IsValid(EntityManager))
                coordinates = transform.Coordinates;
        }

        var ruins = EntityQueryEnumerator<TownHallRuinComponent, TransformComponent>();
        while (ruins.MoveNext(out var uid, out var ruin, out var transform))
        {
            if (ruin.TerritoryId != territory.Id ||
                mapId is { } ruinMap && transform.MapID != ruinMap ||
                !_territories.Contains(territory, transform.Coordinates))
                continue;

            objectives.Add(uid);
            if (!coordinates.IsValid(EntityManager))
                coordinates = transform.Coordinates;
        }

        if (objectives.Count == 0)
            return false;

        var replacement = Spawn(factionPrototype.TownHallPrototype, coordinates);
        if (!TryComp<TownHallComponent>(replacement, out var newHall))
        {
            Del(replacement);
            return false;
        }

        newHall.Configure(territory, faction);
        foreach (var objective in objectives)
        {
            if (TryComp<TownHallComponent>(objective, out var oldHall))
                oldHall.TerritoryId = "Unassigned";
            Del(objective);
        }

        return true;
    }

    private void OnRuinInteract(Entity<TownHallRuinComponent> ruin, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach ||
            !TryComp<ActorComponent>(args.User, out var actor) ||
            !_factions.TryGetFaction(actor.PlayerSession.UserId, out var faction) ||
            _territories.GetState(new TerritoryId(ruin.Comp.TerritoryId)) != TerritoryState.Neutral ||
            !TryComp<StackComponent>(args.Used, out var stack) || stack.StackTypeId != "Steel")
            return;

        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User, TimeSpan.FromSeconds(2),
            new TownHallRepairDoAfterEvent(faction.Id), ruin, target: ruin, used: args.Used));
    }

    private void OnRepairComplete(Entity<TownHallRuinComponent> ruin, ref TownHallRepairDoAfterEvent args)
    {
        var territory = new TerritoryId(ruin.Comp.TerritoryId);
        if (args.Cancelled || args.Used is not { } used ||
            !TryComp<ActorComponent>(args.User, out var actor) ||
            !_factions.TryGetFaction(actor.PlayerSession.UserId, out var faction) || faction.Id != args.FactionId ||
            !_prototypes.TryIndex<FrontlineFactionPrototype>(faction.Id, out var factionPrototype) ||
            _territories.GetState(territory) != TerritoryState.Neutral ||
            !TryComp<StackComponent>(used, out var stack) || stack.StackTypeId != "Steel" ||
            !_stack.TryUse((used, stack), 1))
            return;

        ruin.Comp.DepositedSteel++;
        if (ruin.Comp.DepositedSteel < ruin.Comp.RequiredSteel)
        {
            Dirty(ruin);
            return;
        }

        var hall = Spawn(factionPrototype.TownHallPrototype, Transform(ruin).Coordinates);
        Comp<TownHallComponent>(hall).Configure(territory, faction);
        Del(ruin);
    }
}
