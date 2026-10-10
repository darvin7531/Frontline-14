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
    public Guid Id = Guid.NewGuid();

    // Native YAML supports strings, not Guid; keep the runtime/JSON identity unchanged.
    [DataField("id")]
    private string SerializedId
    {
        get => Id.ToString("D");
        set => Id = Guid.ParseExact(value, "D");
    }

    [DataField(required: true)]
    public ProtoId<FrontlineRefineryRecipePrototype> Recipe;

    [DataField]
    public TimeSpan Remaining;

    // Identical paid batches share one parallel timer and retain their remaining receipt count.
    [DataField]
    public long Batches = 1;

    [DataField]
    public FrontlineProductionAccess Access;

    [DataField]
    public string? Owner;

    [DataField]
    public bool Legacy;

    [DataField]
    public Dictionary<ProtoId<StackPrototype>, int> PaidInputs = new();
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
