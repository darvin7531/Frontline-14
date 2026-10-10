namespace Content.Shared.War;

/// <summary>Server-owned entitlement on the original retained physical entity, never client state.</summary>
[RegisterComponent]
public sealed partial class FrontlineProductionClaimComponent : Component
{
    [DataField]
    public string OwnerUserId = string.Empty;

    [DataField]
    public long CompletedAtUtcTicks;

    // Native YAML uses supported scalar ticks; strategic JSON retains an explicit UTC timestamp.
    public DateTimeOffset CompletedAtUtc
    {
        get => new(CompletedAtUtcTicks, TimeSpan.Zero);
        set => CompletedAtUtcTicks = value.UtcTicks;
    }

    public bool IsPersonal(DateTimeOffset now) => now - CompletedAtUtc < TimeSpan.FromHours(2);
}
