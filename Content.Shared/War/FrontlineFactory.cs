using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared.War;

[Prototype]
public sealed partial class FrontlineFactoryRecipePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = null!;

    [DataField]
    public LocId Name = "frontline-factory-recipe-unknown";

    [DataField(required: true)]
    public Dictionary<ProtoId<Content.Shared.Stacks.StackPrototype>, int> Input = new();

    [DataField(required: true)]
    public EntProtoId Output;

    [DataField(required: true)]
    public int OutputAmount;

    [DataField(required: true)]
    public TimeSpan Duration;
}

[DataDefinition]
public sealed partial class FrontlineFactoryJob
{
    [DataField(required: true)]
    public ProtoId<FrontlineFactoryRecipePrototype> Recipe;

    [DataField]
    public TimeSpan Remaining;
}

[RegisterComponent]
public sealed partial class FrontlineFactoryComponent : Component
{
    public const string InputContainerId = "inputContainer";

    [ViewVariables]
    public Container InputContainer = default!;

    [DataField]
    public List<FrontlineFactoryJob> Jobs = new();

    [DataField]
    public int ProcessingSlots = 1;
}
