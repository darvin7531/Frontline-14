using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public enum FrontlineStockpileUiKey { Key }

[Serializable, NetSerializable]
public sealed class FrontlineStockpileUiState(FrontlineStockpileProductState[] products) : BoundUserInterfaceState
{
    public readonly FrontlineStockpileProductState[] Products = products;
}

[Serializable, NetSerializable]
public sealed class FrontlineStockpileProductState(ProtoId<FrontlineSupplyProductPrototype> product, int amount, bool canWithdraw)
{
    public readonly ProtoId<FrontlineSupplyProductPrototype> Product = product;
    public readonly int Amount = amount;
    public readonly bool CanWithdraw = canWithdraw;
}

[Serializable, NetSerializable]
public sealed class FrontlineStockpileSubmitMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class FrontlineStockpileWithdrawMessage(ProtoId<FrontlineSupplyProductPrototype> product) : BoundUserInterfaceMessage
{
    public readonly ProtoId<FrontlineSupplyProductPrototype> Product = product;
}
