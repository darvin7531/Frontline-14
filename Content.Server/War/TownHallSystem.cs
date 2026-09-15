using System;
using Content.Server.Stack;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Content.Server.War;

public sealed partial class TownHallSystem : EntitySystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private WarFactionSystem _factions = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<TownHallComponent, EntityTerminatingEvent>(OnTownHallTerminating);
        SubscribeLocalEvent<TownHallRuinComponent, AfterInteractEvent>(OnRuinInteract);
        SubscribeLocalEvent<TownHallRuinComponent, TownHallRepairDoAfterEvent>(OnRepairComplete);
    }

    private void OnTownHallTerminating(Entity<TownHallComponent> hall, ref EntityTerminatingEvent args)
    {
        var ruin = Spawn("TownHallRuin", Transform(hall).Coordinates);
        Comp<TownHallRuinComponent>(ruin).TerritoryId = hall.Comp.TerritoryId;
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
        if (args.Cancelled || args.Used is not { } used ||
            !TryComp<StackComponent>(used, out var stack) || stack.StackTypeId != "Steel" ||
            !_stack.TryUse((used, stack), 1))
            return;

        ruin.Comp.DepositedSteel++;
        if (ruin.Comp.DepositedSteel < ruin.Comp.RequiredSteel)
        {
            Dirty(ruin);
            return;
        }

        var territory = new TerritoryId(ruin.Comp.TerritoryId);
        RemComp<TownHallRuinComponent>(ruin);
        AddComp<TownHallComponent>(ruin).Configure(territory, new FactionId(args.FactionId));
    }
}
