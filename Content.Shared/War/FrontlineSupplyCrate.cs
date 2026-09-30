using Robust.Shared.Prototypes;

namespace Content.Shared.War;

/// <summary>
/// A stockpile product. Supplies used for respawning need not have a physical withdrawal entity.
/// </summary>
[Prototype]
public sealed partial class FrontlineSupplyProductPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = null!;

    [DataField(required: true)]
    public LocId Name = "frontline-supply-product-soldier-supplies";

    [DataField]
    public EntProtoId? Entity;
}

/// <summary>
/// Represents a sealed batch, not an inventory of spawned product entities.
/// </summary>
[RegisterComponent]
public sealed partial class FrontlineSupplyCrateComponent : Component
{
    [DataField(required: true)]
    public ProtoId<FrontlineSupplyProductPrototype> Product;

    [DataField(required: true)]
    public int Amount;
}
