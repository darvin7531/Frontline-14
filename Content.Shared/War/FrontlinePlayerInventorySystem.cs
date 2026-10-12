using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Interaction.Components;

namespace Content.Shared.War;

/// <summary>Opt-in inventory layout; the owning API releases removed slots without deleting gear.</summary>
public sealed partial class FrontlinePlayerInventorySystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlinePlayerComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<FrontlinePlayerComponent, DidEquipEvent>(OnEquipped);
    }

    private void OnStartup(Entity<FrontlinePlayerComponent> ent, ref ComponentStartup args)
    {
        if (TryComp<InventoryComponent>(ent, out var inventory))
            _inventory.SetTemplateId((ent, inventory), "frontline");
        if (_inventory.TryGetSlotEntity(ent, "back", out var bag)) Bind(bag.Value);
    }

    private void OnEquipped(Entity<FrontlinePlayerComponent> ent, ref DidEquipEvent args)
    {
        if (args.Slot == "back") Bind(args.Equipment);
    }

    private void Bind(EntityUid bag)
    {
        var bound = EnsureComp<UnremoveableComponent>(bag);
        bound.DeleteOnDrop = false;
        Dirty(bag, bound);
    }
}
