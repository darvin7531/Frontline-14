using System;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Light.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;

namespace Content.Server.War;

public sealed class PersistentWarMapValidatorSystem : EntitySystem
{
    public void Validate(EntityUid map)
    {
        if (!TryComp(map, out MapAtmosphereComponent? atmosphere))
            throw new InvalidOperationException("PersistentWar map must include MapAtmosphere.");

        if (atmosphere.Space)
            throw new InvalidOperationException("PersistentWar map must set MapAtmosphere.space to false.");

        if (atmosphere.Mixture.GetMoles(Gas.Oxygen) <= 0 || atmosphere.Mixture.GetMoles(Gas.Nitrogen) <= 0)
            throw new InvalidOperationException("PersistentWar map must include oxygen and nitrogen.");

        if (!HasComp<MapLightComponent>(map))
            throw new InvalidOperationException("PersistentWar map must include MapLight.");

        if (!TryComp(map, out LightCycleComponent? cycle))
            throw new InvalidOperationException("PersistentWar map must include LightCycle.");

        if (!cycle.Enabled || cycle.Duration <= TimeSpan.Zero)
            throw new InvalidOperationException("PersistentWar map LightCycle must be enabled with a positive duration.");
    }
}