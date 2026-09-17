using Content.Server.GameTicking;
using Content.Shared;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Robust.Shared.Random;

namespace Content.Server.Light.EntitySystems;

/// <inheritdoc/>
public sealed partial class LightCycleSystem : SharedLightCycleSystem
{
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IRobustRandom _random = default!;

    public TimeSpan GetPhase(Entity<LightCycleComponent> cycle)
    {
        if (cycle.Comp.Duration <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var ticks = (_ticker.RoundDuration() + cycle.Comp.Offset).Ticks % cycle.Comp.Duration.Ticks;
        return TimeSpan.FromTicks(ticks < 0 ? ticks + cycle.Comp.Duration.Ticks : ticks);
    }

    public void SetPhase(Entity<LightCycleComponent> cycle, TimeSpan phase)
    {
        if (cycle.Comp.Duration <= TimeSpan.Zero)
            return;

        var offset = phase - _ticker.RoundDuration();
        var ticks = offset.Ticks % cycle.Comp.Duration.Ticks;
        SetOffset(cycle, TimeSpan.FromTicks(ticks < 0 ? ticks + cycle.Comp.Duration.Ticks : ticks));
    }

    protected override void OnCycleMapInit(Entity<LightCycleComponent> ent, ref MapInitEvent args)
    {
        base.OnCycleMapInit(ent, ref args);

        if (ent.Comp.InitialOffset)
        {
            SetOffset(ent, _random.Next(ent.Comp.Duration));
        }
    }
}
