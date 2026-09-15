using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public sealed class RespawnChoiceEuiState : EuiStateBase
{
}

[Serializable, NetSerializable]
public sealed class RespawnNowMessage : EuiMessageBase
{
}
