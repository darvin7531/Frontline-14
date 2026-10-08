namespace Content.Shared.Vehicle.Components;

/// <summary>Opt-in strategic identity; never persists drivers, input relays or runtime entity IDs.</summary>
[RegisterComponent]
public sealed partial class VehiclePersistenceComponent : Component
{
    [DataField]
    public string VehicleId = string.Empty;
}
