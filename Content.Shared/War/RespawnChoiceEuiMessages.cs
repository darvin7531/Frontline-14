using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public sealed class RespawnChoiceEuiState(RespawnBaseOption[] bases) : EuiStateBase
{
    public RespawnBaseOption[] Bases { get; } = bases;
}

[Serializable, NetSerializable]
public sealed class RespawnNowMessage(string territoryId) : EuiMessageBase
{
    public string TerritoryId { get; } = territoryId;
}

[Serializable, NetSerializable]
public readonly record struct RespawnBaseOption(string TerritoryId, LocId Name, int Count);
