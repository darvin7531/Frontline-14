using Content.Shared.Gibbing;
using Content.Shared.War;

namespace Content.Shared.Body;

public sealed partial class GibbableOrganSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GibbableOrganComponent, BodyRelayedEvent<BeingGibbedEvent>>(OnBeingGibbed);
    }

    private void OnBeingGibbed(Entity<GibbableOrganComponent> ent, ref BodyRelayedEvent<BeingGibbedEvent> args)
    {
        if (HasComp<FrontlinePlayerComponent>(args.Body))
            return;
        args.Args.Giblets.Add(ent);
    }
}
