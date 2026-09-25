using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public sealed class WarVictoryEuiState(int warId, FactionId winner, int territoriesHeld, int totalTerritories, TimeSpan duration) : EuiStateBase
{
    public int WarId { get; } = warId;
    public FactionId Winner { get; } = winner;
    public int TerritoriesHeld { get; } = territoriesHeld;
    public int TotalTerritories { get; } = totalTerritories;
    public TimeSpan Duration { get; } = duration;
}

[Serializable, NetSerializable]
public sealed class ReturnToWarLobbyMessage : EuiMessageBase
{
}
