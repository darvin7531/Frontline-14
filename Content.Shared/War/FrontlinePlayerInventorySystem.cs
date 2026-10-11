using Content.Shared.Inventory;

namespace Content.Shared.War;

/// <summary>Opt-in inventory layout; the owning API releases removed slots without deleting gear.</summary>
public sealed partial class FrontlinePlayerInventorySystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlinePlayerComponent, ComponentStartup>(OnStartup);
    }

    private void OnStartup(Entity<FrontlinePlayerComponent> ent, ref ComponentStartup args)
    {
        if (TryComp<InventoryComponent>(ent, out var inventory))
            _inventory.SetTemplateId((ent, inventory), "frontline");
    }
}
