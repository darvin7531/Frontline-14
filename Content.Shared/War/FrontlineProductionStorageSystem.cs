using Content.Shared.Interaction;
using Robust.Shared.Containers;

namespace Content.Shared.War;

/// <summary>Native child windows share their machine's reachability, not an interaction bypass.</summary>
public sealed partial class FrontlineProductionStorageSystem : EntitySystem
{
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedContainerSystem _containers = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineProductionStorageComponent, AccessibleOverrideEvent>(OnAccessible);
    }

    private void OnAccessible(Entity<FrontlineProductionStorageComponent> ent, ref AccessibleOverrideEvent args)
    {
        if (args.Target != ent.Owner || args.Handled)
            return;
        args.Handled = true;
        args.Accessible = !TerminatingOrDeleted(ent.Comp.Machine) &&
            _containers.TryGetContainingContainer(ent.Owner, out var parent) && parent.Owner == ent.Comp.Machine &&
            _interaction.InRangeAndAccessible(args.User, ent.Comp.Machine);
    }
}
