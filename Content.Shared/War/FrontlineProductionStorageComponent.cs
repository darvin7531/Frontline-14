using Robust.Shared.GameStates;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

/// <summary>A machine's native storage child, not a second inventory or a copy of its goods.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class FrontlineProductionStorageComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid Machine;

    [DataField, AutoNetworkedField]
    public bool Output;
}

[Serializable, NetSerializable]
public sealed class FrontlineProductionStorageViewRequestMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class FrontlineProductionStorageViewMessage(NetEntity[] items) : BoundUserInterfaceMessage
{
    public readonly NetEntity[] Items = items;
}
