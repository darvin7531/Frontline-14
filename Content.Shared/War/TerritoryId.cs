using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public readonly record struct TerritoryId(string Id);

[Serializable, NetSerializable]
public enum TerritoryState : byte
{
    Neutral,
    Owned,
    Contested
}
