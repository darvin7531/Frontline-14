using System.Linq;
using Content.Shared.Destructible;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.War;
using Robust.Shared.Containers;

namespace Content.Server.War;

public sealed partial class FrontlineVehicleCargoSystem : EntitySystem
{
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private FrontlineProductionSystem _production = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineVehicleCargoComponent, ContainerIsInsertingAttemptEvent>(OnInserting);
        SubscribeLocalEvent<FrontlineVehicleCargoComponent, ContainerIsRemovingAttemptEvent>(OnRemoving);
        SubscribeLocalEvent<FrontlineVehicleCargoComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<FrontlineVehicleCargoComponent, EntRemovedFromContainerMessage>(OnRemoved);
        SubscribeLocalEvent<FrontlineVehicleCargoComponent, BreakageEventArgs>(OnBreak);
    }

    private void OnInserting(Entity<FrontlineVehicleCargoComponent> ent, ref ContainerIsInsertingAttemptEvent args)
    {
        if (args.Container != Comp<StorageComponent>(ent).Container)
            return;
        if (_production.NativeTransferActive(ent) && !_production.NativeTransferItem(ent, args.EntityUid) ||
            args.Container.ContainedEntities.Count >= 10 ||
            !(HasComp<FrontlineSupplyCrateComponent>(args.EntityUid) ||
              TryComp<StackComponent>(args.EntityUid, out var stack) && !stack.Unlimited && stack.Count > 0))
            args.Cancel();
    }

    private void OnRemoving(Entity<FrontlineVehicleCargoComponent> ent, ref ContainerIsRemovingAttemptEvent args)
    {
        if (args.Container == Comp<StorageComponent>(ent).Container &&
            _production.NativeTransferActive(ent) && !_production.NativeTransferItem(ent, args.EntityUid)) args.Cancel();
    }

    private void OnInserted(Entity<FrontlineVehicleCargoComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container != Comp<StorageComponent>(ent).Container || ent.Comp.CargoIds.ContainsKey(args.Entity))
            return;
        for (var i = 1; i <= 10; i++)
        {
            var id = $"cargo{i:00}";
            if (ent.Comp.CargoIds.ContainsValue(id)) continue;
            ent.Comp.CargoIds.Add(args.Entity, id);
            return;
        }
        throw new InvalidOperationException("Cargo capacity and identities disagree.");
    }

    private void OnRemoved(Entity<FrontlineVehicleCargoComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container == Comp<StorageComponent>(ent).Container)
            ent.Comp.CargoIds.Remove(args.Entity);
    }

    private void OnBreak(Entity<FrontlineVehicleCargoComponent> ent, ref BreakageEventArgs args)
    {
        var contents = Comp<StorageComponent>(ent).Container.ContainedEntities.ToArray();
        if (contents.Length == 0) return;
        var bag = Spawn("FrontlineVehicleLootBag", Transform(ent).Coordinates);
        foreach (var item in contents)
        {
            if (!_storage.Insert(bag, item, out _, stackAutomatically: false) ||
                !Comp<StorageComponent>(bag).Container.Contains(item))
                throw new InvalidOperationException("Vehicle loot bag did not retain its original cargo.");
        }
    }
}
