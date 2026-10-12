using System.Linq;
using Content.Shared.Clothing.Components;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Item;
using Content.Shared.Mobs;
using Content.Shared.Storage;
using Content.Shared.War;
using Robust.Shared.Containers;

namespace Content.Server.War;

/// <summary>The original backpack becomes ground loot; empty checks wait until transfers finish.</summary>
public sealed partial class FrontlinePlayerLootSystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    private readonly HashSet<EntityUid> _checkEmpty = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlinePlayerComponent, MobStateChangedEvent>(OnDeath);
        SubscribeLocalEvent<FrontlinePlayerComponent, DidUnequipEvent>(OnUnequipped);
        SubscribeLocalEvent<FrontlineLootBagComponent, ComponentStartup>(OnLootStartup);
        SubscribeLocalEvent<FrontlineLootBagComponent, EntRemovedFromContainerMessage>(OnLootRemoved);
    }

    private void OnDeath(Entity<FrontlinePlayerComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
            _inventory.TryUnequip(ent, "back", silent: true, force: true);
    }

    private void OnUnequipped(Entity<FrontlinePlayerComponent> ent, ref DidUnequipEvent args)
    {
        if (args.Slot != "back" || TerminatingOrDeleted(args.Equipment)) return;
        RemComp<UnremoveableComponent>(args.Equipment);
        RemComp<ItemComponent>(args.Equipment);
        RemComp<ClothingComponent>(args.Equipment);
        EnsureComp<FrontlineLootBagComponent>(args.Equipment);
    }

    private void OnLootStartup(Entity<FrontlineLootBagComponent> ent, ref ComponentStartup args) =>
        _checkEmpty.Add(ent);

    private void OnLootRemoved(Entity<FrontlineLootBagComponent> ent, ref EntRemovedFromContainerMessage args) =>
        _checkEmpty.Add(ent);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var bag in _checkEmpty.ToArray())
        {
            _checkEmpty.Remove(bag);
            if (!TerminatingOrDeleted(bag) && !_containers.IsEntityInContainer(bag) &&
                TryComp<StorageComponent>(bag, out var storage) && storage.Container.ContainedEntities.Count == 0)
                QueueDel(bag);
        }
    }
}

[RegisterComponent]
public sealed partial class FrontlineLootBagComponent : Component;
