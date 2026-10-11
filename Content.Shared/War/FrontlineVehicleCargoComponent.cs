using Robust.Shared.GameStates;

namespace Content.Shared.War;

[RegisterComponent, NetworkedComponent]
public sealed partial class FrontlineVehicleCargoComponent : Component
{
    // Durable legacy cargo01..cargo10 identities, independent of the driver's container.
    [DataField]
    public Dictionary<EntityUid, string> CargoIds = new();
}
