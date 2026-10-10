using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared.War;

public enum FrontlineProductionAccess : byte
{
    Public,
    Personal,
}

[Prototype]
public sealed partial class FrontlineFactoryRecipePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = null!;

    [DataField]
    public LocId Name = "frontline-factory-recipe-unknown";

    [DataField]
    public LocId Category = "frontline-ui-category-other";

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
    public Guid Id = Guid.NewGuid();

    // Native YAML supports strings, not Guid; keep the runtime/JSON identity unchanged.
    [DataField("id")]
    private string SerializedId
    {
        get => Id.ToString("D");
        set => Id = Guid.ParseExact(value, "D");
    }

    [DataField(required: true)]
    public ProtoId<FrontlineFactoryRecipePrototype> Recipe;

    [DataField]
    public TimeSpan Remaining;

    [DataField]
    public FrontlineProductionAccess Access;

    // Persist the authenticated account identity, never a client-provided faction or entity.
    [DataField]
    public string? Owner;

    [DataField]
    public bool Legacy;

    [DataField]
    public Dictionary<ProtoId<Content.Shared.Stacks.StackPrototype>, int> PaidInputs = new();
}

[RegisterComponent]
public sealed partial class FrontlineFactoryComponent : Component
{
    public const string InputContainerId = "inputContainer";
    public const string OutputContainerId = "outputContainer";

    [ViewVariables]
    public Container InputContainer = default!;

    [ViewVariables]
    public Container OutputContainer = default!;

    /// <summary>Stable mapper identity within one campaign map; never a runtime UID or position.</summary>
    [DataField]
    public string FactoryId = "";

    [DataField]
    public List<FrontlineFactoryJob> Jobs = new();

    [DataField]
    public int ProcessingSlots = 1;
}
