using Content.Shared.Stacks;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public enum FrontlineRefineryUiKey
{
    Key,
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryUiState(
    FrontlineRefineryInputState[] inputs,
    FrontlineRefineryRecipeState[] recipes,
    FrontlineRefineryJobState[] jobs) : BoundUserInterfaceState
{
    public readonly FrontlineRefineryInputState[] Inputs = inputs;
    public readonly FrontlineRefineryRecipeState[] Recipes = recipes;
    public readonly FrontlineRefineryJobState[] Jobs = jobs;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryInputState(ProtoId<StackPrototype> stack, int amount)
{
    public readonly ProtoId<StackPrototype> Stack = stack;
    public readonly int Amount = amount;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryRecipeState(
    ProtoId<FrontlineRefineryRecipePrototype> id,
    FrontlineRefineryInputState[] inputs,
    FrontlineRefineryInputState output,
    TimeSpan duration,
    bool canSubmit)
{
    public readonly ProtoId<FrontlineRefineryRecipePrototype> Id = id;
    public readonly FrontlineRefineryInputState[] Inputs = inputs;
    public readonly FrontlineRefineryInputState Output = output;
    public readonly TimeSpan Duration = duration;
    public readonly bool CanSubmit = canSubmit;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryJobState(
    ProtoId<FrontlineRefineryRecipePrototype> recipe,
    TimeSpan remaining,
    bool processing)
{
    public readonly ProtoId<FrontlineRefineryRecipePrototype> Recipe = recipe;
    public readonly TimeSpan Remaining = remaining;
    public readonly bool Processing = processing;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefinerySubmitMessage(ProtoId<FrontlineRefineryRecipePrototype> recipe)
    : BoundUserInterfaceMessage
{
    public readonly ProtoId<FrontlineRefineryRecipePrototype> Recipe = recipe;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryEjectMessage : BoundUserInterfaceMessage;
