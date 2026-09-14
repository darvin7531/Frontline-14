using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public sealed class FactionSelectionEuiState(FactionSelectionOption[] factions) : EuiStateBase
{
    public FactionSelectionOption[] Factions { get; } = factions;
}

[Serializable, NetSerializable]
public sealed class ChooseFactionMessage(FactionId faction) : EuiMessageBase
{
    public FactionId Faction { get; } = faction;
}

[Serializable, NetSerializable]
public readonly record struct FactionSelectionOption(FactionId Id, string Name, string Description, Color Color);
