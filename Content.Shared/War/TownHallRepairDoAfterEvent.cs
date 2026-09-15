using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public sealed partial class TownHallRepairDoAfterEvent : SimpleDoAfterEvent
{
    public string FactionId { get; }

    public TownHallRepairDoAfterEvent(string factionId)
    {
        FactionId = factionId;
    }
}