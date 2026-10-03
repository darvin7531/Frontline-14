using Content.Shared.Stacks;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public enum FrontlineFactoryUiKey
{
    Key,
}

[Serializable, NetSerializable]
public sealed class FrontlineFactoryUiState(
    FrontlineFactoryInputState[] inputs,
    FrontlineFactoryRecipeState[] recipes,
    FrontlineFactoryJobState[] jobs,
    FrontlineFactoryOutputState[]? outputs = null) : BoundUserInterfaceState
{
    public readonly FrontlineFactoryInputState[] Inputs = inputs;
    public readonly FrontlineFactoryRecipeState[] Recipes = recipes;
    public readonly FrontlineFactoryJobState[] Jobs = jobs;
    public readonly FrontlineFactoryOutputState[] Outputs = outputs ?? [];
}

[Serializable, NetSerializable]
public sealed class FrontlineFactoryOutputState(EntProtoId prototype, int count)
{
    public readonly EntProtoId Prototype = prototype;
    public readonly int Count = count;
}

[Serializable, NetSerializable]
public sealed class FrontlineFactoryInputState(ProtoId<StackPrototype> stack, int amount)
{
    public readonly ProtoId<StackPrototype> Stack = stack;
    public readonly int Amount = amount;
}

[Serializable, NetSerializable]
public sealed class FrontlineFactoryRecipeState(
    ProtoId<FrontlineFactoryRecipePrototype> id,
    FrontlineFactoryInputState[] inputs,
    EntProtoId output,
    int outputAmount,
    TimeSpan duration,
    bool canSubmit)
{
    public readonly ProtoId<FrontlineFactoryRecipePrototype> Id = id;
    public readonly FrontlineFactoryInputState[] Inputs = inputs;
    public readonly EntProtoId Output = output;
    public readonly int OutputAmount = outputAmount;
    public readonly TimeSpan Duration = duration;
    public readonly bool CanSubmit = canSubmit;
}

[Serializable, NetSerializable]
public sealed class FrontlineFactoryJobState(
    ProtoId<FrontlineFactoryRecipePrototype> recipe,
    TimeSpan remaining,
    bool processing)
{
    public readonly ProtoId<FrontlineFactoryRecipePrototype> Recipe = recipe;
    public readonly TimeSpan Remaining = remaining;
    public readonly bool Processing = processing;
}

[Serializable, NetSerializable]
public sealed class FrontlineFactorySubmitMessage(ProtoId<FrontlineFactoryRecipePrototype> recipe)
    : BoundUserInterfaceMessage
{
    public readonly ProtoId<FrontlineFactoryRecipePrototype> Recipe = recipe;
}

[Serializable, NetSerializable]
public sealed class FrontlineFactoryEjectMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class FrontlineFactoryTakeOutputMessage : BoundUserInterfaceMessage;
