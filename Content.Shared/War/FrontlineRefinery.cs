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
    public LocId Name = "frontline-refinery-recipe-unknown";

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
    public const string OutputContainerId = "outputContainer";

    // Mapper identity, not a runtime entity or position.
    [DataField]
    public string RefineryId = string.Empty;

    [ViewVariables]
    public Container InputContainer = default!;

    [ViewVariables]
    public Container OutputContainer = default!;

    [DataField]
    public List<FrontlineRefineryJob> Jobs = new();

    [DataField]
    public int ProcessingSlots = 1;
}
