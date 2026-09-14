using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public readonly record struct FactionId(string Id)
{
    public override string ToString() => Id;
}
