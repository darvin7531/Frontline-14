using System.Collections.Immutable;
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
    FrontlineRefineryJobState[] jobs,
    ImmutableArray<FrontlineRefineryInputState> outputs = default,
    int outputStackCount = 0) : BoundUserInterfaceState
{
    public readonly FrontlineRefineryInputState[] Inputs = inputs;
    public readonly FrontlineRefineryRecipeState[] Recipes = recipes;
    public readonly FrontlineRefineryJobState[] Jobs = jobs;
    public readonly ImmutableArray<FrontlineRefineryInputState> Outputs = outputs.IsDefault ? [] : outputs;
    public readonly int OutputStackCount = outputStackCount;
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
    bool processing, long batches = 1, Guid id = default, bool canCancel = false)
{
    public readonly Guid Id = id;
    public readonly bool CanCancel = canCancel;
    public readonly ProtoId<FrontlineRefineryRecipePrototype> Recipe = recipe;
    public readonly TimeSpan Remaining = remaining;
    public readonly bool Processing = processing;
    public readonly long Batches = batches;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefinerySubmitMessage(ProtoId<FrontlineRefineryRecipePrototype> recipe, int batches = 1, bool allAvailable = false, bool personal = false)
    : BoundUserInterfaceMessage
{
    public readonly ProtoId<FrontlineRefineryRecipePrototype> Recipe = recipe;
    public readonly int Batches = batches;
    public readonly bool AllAvailable = allAvailable;
    public readonly bool Personal = personal;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryEjectMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class FrontlineRefineryTakeOutputMessage(bool personal = false) : BoundUserInterfaceMessage
{
    public readonly bool Personal = personal;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryModeMessage(bool personal) : BoundUserInterfaceMessage
{
    public readonly bool Personal = personal;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryViewMessage(FrontlineRefineryUiState state, bool personal) : BoundUserInterfaceMessage
{
    public readonly FrontlineRefineryUiState State = state;
    public readonly bool Personal = personal;
}

[Serializable, NetSerializable]
public sealed class FrontlineRefineryCancelMessage(Guid id, bool personal = false) : BoundUserInterfaceMessage
{
    public readonly Guid Id = id;
    public readonly bool Personal = personal;
}
