using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared.War;

[Prototype]
public sealed partial class FrontlineRefineryRecipePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = null!;

    [DataField]
    public LocId Name;

    [DataField(required: true)]
    public Dictionary<ProtoId<StackPrototype>, int> Input = new();

    [DataField(required: true)]
    public Dictionary<ProtoId<StackPrototype>, int> Output = new();

    [DataField(required: true)]
    public TimeSpan Duration;
}

[DataDefinition]
public sealed partial class FrontlineRefineryJob
{
    [DataField(required: true)]
    public ProtoId<FrontlineRefineryRecipePrototype> Recipe;

    [DataField]
    public TimeSpan Remaining;
}

[RegisterComponent]
public sealed partial class FrontlineRefineryComponent : Component
{
    public const string InputContainerId = "inputContainer";

    [ViewVariables]
    public Container InputContainer = default!;

    [DataField]
    public List<FrontlineRefineryJob> Jobs = new();

    [DataField]
    public int ProcessingSlots = 1;
}
