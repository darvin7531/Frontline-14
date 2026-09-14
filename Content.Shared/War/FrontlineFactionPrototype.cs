using Robust.Shared.Prototypes;

namespace Content.Shared.War;

[Prototype("frontlineFaction")]
public sealed partial class FrontlineFactionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Description = string.Empty;

    [DataField(required: true)]
    public Color Color;
}
