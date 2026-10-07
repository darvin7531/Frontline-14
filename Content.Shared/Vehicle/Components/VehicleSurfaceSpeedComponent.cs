using Content.Shared.Maps;
using Content.Shared.Vehicle.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Vehicle.Components;

/// <summary>
/// Opt-in ground-surface speed policy; native vehicle movement remains unchanged.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, Access(typeof(VehicleSurfaceSpeedSystem))]
public sealed partial class VehicleSurfaceSpeedComponent : Component
{
    [DataField, AutoNetworkedField]
    public HashSet<ProtoId<ContentTileDefinition>> RoadTiles = new();

    [DataField, AutoNetworkedField]
    public float OffRoadModifier = 0.55f;
}
