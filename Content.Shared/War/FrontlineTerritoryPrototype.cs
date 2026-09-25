using Robust.Shared.Prototypes;

namespace Content.Shared.War;

[Prototype]
public sealed partial class FrontlineTerritoryPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Description = string.Empty;

    [DataField(required: true)]
    public int VictoryPoints;
}

[Prototype]
public sealed partial class FrontlineWarPrototype : IPrototype
{
    public const string MainWar = "MainWar";

    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public List<ProtoId<FrontlineTerritoryPrototype>> Territories = new();

    [DataField(required: true)]
    public int RequiredVictoryPoints;
}
