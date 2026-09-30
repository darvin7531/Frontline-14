using Robust.Shared.Prototypes;

namespace Content.Shared.War;

[RegisterComponent]
public sealed partial class FrontlineStockpileComponent : Component
{
    [DataField]
    public Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int> Counts = new();
}
