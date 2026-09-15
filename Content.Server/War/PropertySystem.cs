using System;
using Content.Shared.Construction.Components;
using Content.Shared.War;
using Robust.Shared.GameObjects;

namespace Content.Server.War;

public sealed partial class PropertySystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<PropertyComponent, UnanchorAttemptEvent>(OnUnanchorAttempt);
    }

    public void Claim(EntityUid entity, PropertyId property, Guid owner)
    {
        EnsureComp<PropertyComponent>(entity).Claim(property, owner);
    }

    private void OnUnanchorAttempt(Entity<PropertyComponent> property, ref UnanchorAttemptEvent args)
    {
        if (!TryComp<ActorComponent>(args.User, out var actor) || property.Comp.OwnerAccountId != actor.PlayerSession.UserId.UserId)
            args.Cancel();
    }
}