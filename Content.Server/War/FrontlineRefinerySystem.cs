using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Content.Server.Stack;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Content.Shared.War;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class FrontlineRefinerySystem : EntitySystem
{
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly HashSet<EntityUid> _active = new();
    private readonly HashSet<EntityUid> _submitting = new();
    private readonly HashSet<EntityUid> _completing = new();
    private readonly HashSet<EntityUid> _taking = new();
    private readonly HashSet<EntityUid> _movingInputs = new();
    private readonly HashSet<EntityUid> _restoring = new();
    private float _uiUpdateAccumulator;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineRefineryComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<FrontlineRefineryComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<FrontlineRefineryComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<FrontlineRefineryComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<FrontlineRefineryComponent, BeforeActivatableUIOpenEvent>(OnBeforeUiOpen);
        SubscribeLocalEvent<FrontlineRefineryComponent, FrontlineRefinerySubmitMessage>(OnSubmitMessage);
        SubscribeLocalEvent<FrontlineRefineryComponent, FrontlineRefineryEjectMessage>(OnEjectMessage);
        SubscribeLocalEvent<FrontlineRefineryComponent, FrontlineRefineryTakeOutputMessage>(OnTakeOutputMessage);
        SubscribeLocalEvent<FrontlineRefineryComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<FrontlineRefineryComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<TransformComponent, StackCountChangedEvent>(OnOutputCountChanged);
    }

    private void OnStartup(Entity<FrontlineRefineryComponent> refinery, ref ComponentStartup args)
    {
        refinery.Comp.InputContainer = _containers.EnsureContainer<Container>(
            refinery,
            FrontlineRefineryComponent.InputContainerId);
        refinery.Comp.OutputContainer = _containers.EnsureContainer<Container>(
            refinery,
            FrontlineRefineryComponent.OutputContainerId);
    }

    private void OnMapInit(Entity<FrontlineRefineryComponent> refinery, ref MapInitEvent args)
    {
        if (refinery.Comp.Jobs.Count > 0)
            _active.Add(refinery);
    }

    private void OnTerminating(Entity<FrontlineRefineryComponent> refinery, ref EntityTerminatingEvent args)
    {
        _active.Remove(refinery);
    }

    private void OnInteractUsing(Entity<FrontlineRefineryComponent> refinery, ref InteractUsingEvent args)
    {
        if (args.Handled || _restoring.Contains(refinery.Owner))
            return;

        if (!CanAcceptInput(args.Used))
        {
            _popup.PopupEntity(Loc.GetString("frontline-refinery-invalid-input"), refinery.Owner, args.User);
            return;
        }

        if (!_movingInputs.Add(refinery.Owner))
            return;
        try
        {
            if (_hands.TryDropIntoContainer(args.User, args.Used, refinery.Comp.InputContainer))
                args.Handled = true;
        }
        finally
        {
            _movingInputs.Remove(refinery.Owner);
        }
    }

    private void OnBeforeUiOpen(Entity<FrontlineRefineryComponent> refinery, ref BeforeActivatableUIOpenEvent args)
    {
        UpdateUi(refinery);
    }

    private void OnSubmitMessage(Entity<FrontlineRefineryComponent> refinery, ref FrontlineRefinerySubmitMessage args)
    {
        if (!TrySubmitPlayerJob(refinery.Owner, args.Actor, args.Recipe))
            _popup.PopupEntity(Loc.GetString("frontline-refinery-insufficient-input"), refinery.Owner, args.Actor);

        UpdateUi(refinery);
    }

    private void OnEjectMessage(Entity<FrontlineRefineryComponent> refinery, ref FrontlineRefineryEjectMessage args)
    {
        if (TryEjectPlayerInputs(refinery.Owner, args.Actor))
            UpdateUi(refinery);
    }

    private void OnTakeOutputMessage(Entity<FrontlineRefineryComponent> refinery, ref FrontlineRefineryTakeOutputMessage args)
    {
        TryTakePlayerOutput(refinery.Owner, args.Actor);
    }

    /// <summary>
    /// Public take-next operation: the server selects the retained stack, never a client entity ID.
    /// </summary>
    public bool TryTakePlayerOutput(EntityUid refineryUid, EntityUid playerUid)
    {
        if (!IsCompletionEntityAlive(refineryUid) || !IsCompletionEntityAlive(playerUid) ||
            _completing.Contains(refineryUid) || _restoring.Contains(refineryUid) ||
            !TryComp<FrontlineRefineryComponent>(refineryUid, out var refinery) ||
            !_taking.Add(refineryUid))
            return false;

        EntityUid? output = null;
        StackComponent? stack = null;
        var count = 0;
        var taken = false;
        var container = refinery.OutputContainer;
        try
        {
            var hand = _hands.GetActiveHand(playerUid);
            if (hand == null || !CanAccessOutput() || _hands.GetActiveItem(playerUid) != null)
                return false;

            foreach (var uid in container.ContainedEntities)
            {
                if (!IsCompletionEntityAlive(uid) ||
                    !TryComp<StackComponent>(uid, out var candidate) || candidate.Unlimited || candidate.Count <= 0)
                    continue;

                output = uid;
                stack = candidate;
                count = candidate.Count;
                break;
            }

            if (output is not { } item || stack == null)
                return false;

            var type = stack.StackTypeId;
            if (!_hands.CanPickupToHand(playerUid, item, hand) ||
                !CanAccessOutput() || _hands.GetActiveHand(playerUid) != hand ||
                _hands.GetActiveItem(playerUid) != null || !container.Contains(item) || !StackUnchanged())
                return false;

            // TryPickup repeats native permission checks and dispatches equip/container callbacks.
            taken = _hands.TryPickup(playerUid, item, hand, animate: false) &&
                    CanAccessOutput() && StackUnchanged() &&
                    _hands.GetActiveHand(playerUid) == hand &&
                    _hands.GetActiveItem(playerUid) == item && !container.Contains(item);
            return taken;

            bool StackUnchanged() => IsCompletionEntityAlive(item) &&
                TryComp<StackComponent>(item, out var current) && current == stack &&
                !current.Unlimited && current.StackTypeId == type && current.Count == count;
        }
        finally
        {
            try
            {
                if (!taken && output is { } item && stack != null &&
                    IsCompletionEntityAlive(item) && CanRestoreOutput())
                {
                    if (stack.Count != count)
                        _stack.SetCount((item, stack), count);

                    // Restore the same entity, not a replacement or a floor drop.
                    if (IsCompletionEntityAlive(item) && CanRestoreOutput() &&
                        !container.Contains(item))
                    {
                        if (_containers.TryGetContainingContainer(item, out var current))
                            _containers.Remove(item, current, reparent: false, force: true);

                        if (IsCompletionEntityAlive(item) && CanRestoreOutput() &&
                            (!_containers.Insert(item, container, force: true) || !container.Contains(item)))
                            Log.Error($"Failed to restore retained refinery output {item} to {refineryUid}.");
                    }
                }
            }
            finally
            {
                _taking.Remove(refineryUid);
                if (IsCompletionEntityAlive(refineryUid) &&
                    TryComp<FrontlineRefineryComponent>(refineryUid, out var current) && current == refinery)
                    UpdateUi((refineryUid, refinery));
            }
        }

        bool CanAccessOutput() => IsCompletionEntityAlive(refineryUid) && IsCompletionEntityAlive(playerUid) &&
            !_completing.Contains(refineryUid) && _interaction.InRangeUnobstructed(playerUid, refineryUid) &&
            IsCompletionEntityAlive(refineryUid) && IsCompletionEntityAlive(playerUid) &&
            TryComp<FrontlineRefineryComponent>(refineryUid, out var current) && current == refinery &&
            _containers.TryGetContainer(refineryUid, FrontlineRefineryComponent.OutputContainerId, out var retained) &&
            retained == container;

        // A queued machine still owns its outputs until termination; restore, never hand them out.
        bool CanRestoreOutput() => !TerminatingOrDeleted(refineryUid) &&
            TryComp<FrontlineRefineryComponent>(refineryUid, out var current) && current == refinery &&
            _containers.TryGetContainer(refineryUid, FrontlineRefineryComponent.OutputContainerId, out var retained) &&
            retained == container;
    }

    private void OnOutputCountChanged(Entity<TransformComponent> stack, ref StackCountChangedEvent args)
    {
        if (_containers.TryGetContainingContainer(stack.Owner, out var container) &&
            container.ID == FrontlineRefineryComponent.OutputContainerId &&
            IsCompletionEntityAlive(container.Owner) &&
            TryComp<FrontlineRefineryComponent>(container.Owner, out var refinery) &&
            refinery.OutputContainer == container)
            UpdateUi((container.Owner, refinery));
    }

    private void OnContainerChanged(Entity<FrontlineRefineryComponent> refinery, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == FrontlineRefineryComponent.InputContainerId ||
            args.Container.ID == FrontlineRefineryComponent.OutputContainerId)
            UpdateUi(refinery);
    }

    private void OnContainerChanged(Entity<FrontlineRefineryComponent> refinery, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == FrontlineRefineryComponent.InputContainerId ||
            args.Container.ID == FrontlineRefineryComponent.OutputContainerId)
            UpdateUi(refinery);
    }

    public IEnumerable<FrontlineRefineryRecipePrototype> GetAvailableRecipes() =>
        _prototypes.EnumeratePrototypes<FrontlineRefineryRecipePrototype>().Where(IsValidRecipe);

    public IReadOnlyList<FrontlineRefineryJob> GetJobs(EntityUid refineryUid) =>
        TryComp<FrontlineRefineryComponent>(refineryUid, out var refinery)
            ? refinery.Jobs.Select(job => new FrontlineRefineryJob
            {
                Recipe = job.Recipe,
                Remaining = job.Remaining,
            }).ToArray()
            : Array.Empty<FrontlineRefineryJob>();

    public FrontlineRefineryUiState BuildUiState(EntityUid refineryUid)
    {
        if (!TryComp<FrontlineRefineryComponent>(refineryUid, out var refinery))
            return new FrontlineRefineryUiState([], [], []);

        var amounts = new Dictionary<ProtoId<StackPrototype>, int>();
        foreach (var uid in refinery.InputContainer.ContainedEntities)
        {
            if (!TryComp<StackComponent>(uid, out var stack) || stack.Unlimited)
                continue;

            amounts[stack.StackTypeId] = amounts.GetValueOrDefault(stack.StackTypeId) + stack.Count;
        }

        var inputs = amounts
            .Select(entry => new FrontlineRefineryInputState(entry.Key, entry.Value))
            .ToArray();
        var recipes = GetAvailableRecipes()
            .Select(recipe =>
            {
                var output = recipe.Output.Single();
                return new FrontlineRefineryRecipeState(
                    recipe.ID,
                    recipe.Input.Select(entry => new FrontlineRefineryInputState(entry.Key, entry.Value)).ToArray(),
                    new FrontlineRefineryInputState(output.Key, output.Value),
                    recipe.Duration,
                    recipe.Input.All(entry => amounts.GetValueOrDefault(entry.Key) >= entry.Value));
            })
            .ToArray();
        var processing = Math.Max(1, refinery.ProcessingSlots);
        var jobs = refinery.Jobs
            .Where(job => _prototypes.TryIndex(job.Recipe, out FrontlineRefineryRecipePrototype? recipe) && IsValidRecipe(recipe))
            .Select((job, index) => new FrontlineRefineryJobState(job.Recipe, job.Remaining, index < processing))
            .ToArray();

        var outputAmounts = new Dictionary<ProtoId<StackPrototype>, int>();
        var outputStackCount = 0;
        if (!_completing.Contains(refineryUid))
        {
            foreach (var uid in refinery.OutputContainer.ContainedEntities)
            {
                if (!IsCompletionEntityAlive(uid) ||
                    !TryComp<StackComponent>(uid, out var stack) || stack.Unlimited || stack.Count <= 0)
                    continue;

                outputAmounts[stack.StackTypeId] = outputAmounts.GetValueOrDefault(stack.StackTypeId) + stack.Count;
                outputStackCount++;
            }
        }

        return new FrontlineRefineryUiState(inputs, recipes, jobs,
            outputAmounts.Select(entry => new FrontlineRefineryInputState(entry.Key, entry.Value)).ToImmutableArray(),
            outputStackCount);
    }

    private void UpdateUi(Entity<FrontlineRefineryComponent> refinery)
    {
        _ui.SetUiState(refinery.Owner, FrontlineRefineryUiKey.Key, BuildUiState(refinery.Owner));
    }

    private bool IsValidRecipe(FrontlineRefineryRecipePrototype recipe)
    {
        return recipe.Duration > TimeSpan.Zero &&
               recipe.Input.Count > 0 &&
               recipe.Output.Count == 1 &&
               recipe.Input.All(entry => entry.Value > 0 && IsSpawnableStack(entry.Key)) &&
               recipe.Output.All(entry => entry.Value > 0 && IsSpawnableStack(entry.Key));
    }

    private bool IsSpawnableStack(ProtoId<StackPrototype> stackId)
    {
        return !string.IsNullOrWhiteSpace(stackId.Id) && _prototypes.TryIndex(stackId, out var stack) &&
               !stack.Abstract &&
               !string.IsNullOrWhiteSpace(stack.Spawn.Id) &&
               _prototypes.TryIndex<EntityPrototype>(stack.Spawn, out var spawn) && !spawn.Abstract &&
               spawn.TryGetComponent<StackComponent>(out var component, _componentFactory) &&
               !component.Unlimited && component.StackTypeId == stackId;
    }

    public bool TryInsertInput(EntityUid refineryUid, EntityUid inputUid)
    {
        if (!TryComp<FrontlineRefineryComponent>(refineryUid, out var refinery) ||
            _restoring.Contains(refineryUid) ||
            TerminatingOrDeleted(refineryUid) ||
            TerminatingOrDeleted(inputUid) ||
            !CanAcceptInput(inputUid) || !_movingInputs.Add(refineryUid))
            return false;

        try
        {
            return _containers.Insert(inputUid, refinery.InputContainer);
        }
        finally
        {
            _movingInputs.Remove(refineryUid);
        }
    }

    private bool CanAcceptInput(EntityUid inputUid)
    {
        return TryComp<StackComponent>(inputUid, out var stack) &&
               !stack.Unlimited &&
               GetAvailableRecipes().Any(recipe => recipe.Input.ContainsKey(stack.StackTypeId));
    }

    public void EjectInputs(EntityUid refineryUid)
    {
        if (_restoring.Contains(refineryUid) || !TryComp<FrontlineRefineryComponent>(refineryUid, out var refinery) ||
            !_movingInputs.Add(refineryUid))
            return;

        try
        {
            _containers.EmptyContainer(refinery.InputContainer);
        }
        finally
        {
            _movingInputs.Remove(refineryUid);
        }
    }

    public bool TryEjectPlayerInputs(EntityUid refineryUid, EntityUid playerUid)
    {
        if (!_interaction.InRangeUnobstructed(playerUid, refineryUid))
            return false;

        EjectInputs(refineryUid);
        return true;
    }

    public bool TrySubmitContainedJob(EntityUid refineryUid, ProtoId<FrontlineRefineryRecipePrototype> recipeId)
    {
        return TryComp<FrontlineRefineryComponent>(refineryUid, out var refinery) &&
               TrySubmitJob(refineryUid, recipeId, refinery.InputContainer.ContainedEntities.ToArray());
    }

    public bool TrySubmitPlayerJob(EntityUid refineryUid,
        EntityUid playerUid,
        ProtoId<FrontlineRefineryRecipePrototype> recipeId)
    {
        return _interaction.InRangeUnobstructed(playerUid, refineryUid) &&
               TrySubmitContainedJob(refineryUid, recipeId);
    }

    /// <summary>
    /// The caller must provide authorized physical inputs. Player-facing callers must not pass
    /// arbitrary client-selected entity IDs without server-side authorization.
    /// </summary>
    public bool TrySubmitJob(
        EntityUid refineryUid,
        ProtoId<FrontlineRefineryRecipePrototype> recipeId,
        IReadOnlyCollection<EntityUid> inputs)
    {
        if (!TryComp<FrontlineRefineryComponent>(refineryUid, out var refinery) ||
            _restoring.Contains(refineryUid) ||
            TerminatingOrDeleted(refineryUid) ||
            !_prototypes.TryIndex(recipeId, out var recipe) ||
            !IsValidRecipe(recipe) ||
            !_submitting.Add(refineryUid))
            return false;

        try
        {
            return TrySubmitValidated(refineryUid, refinery, recipeId, recipe, inputs);
        }
        finally
        {
            _submitting.Remove(refineryUid);
        }
    }

    private bool TrySubmitValidated(
        EntityUid refineryUid,
        FrontlineRefineryComponent refinery,
        ProtoId<FrontlineRefineryRecipePrototype> recipeId,
        FrontlineRefineryRecipePrototype recipe,
        IReadOnlyCollection<EntityUid> inputs)
    {
        var coordinates = Transform(refineryUid).Coordinates;

        var stacks = new Dictionary<EntityUid, StackComponent>();
        foreach (var uid in inputs)
        {
            if (!stacks.ContainsKey(uid) &&
                TryComp<StackComponent>(uid, out var stack) &&
                !stack.Unlimited &&
                !TerminatingOrDeleted(uid))
                stacks.Add(uid, stack);
        }

        var consumption = new List<(EntityUid Uid, StackComponent Stack, int Amount)>();
        foreach (var (stackType, required) in recipe.Input)
        {
            if (required <= 0 || !_prototypes.HasIndex<StackPrototype>(stackType))
                return false;

            var remaining = required;
            foreach (var (uid, stack) in stacks)
            {
                if (stack.StackTypeId != stackType || remaining == 0)
                    continue;

                var amount = Math.Min(stack.Count, remaining);
                consumption.Add((uid, stack, amount));
                remaining -= amount;
            }

            if (remaining != 0)
                return false;
        }

        foreach (var (stackType, amount) in recipe.Output)
        {
            if (amount <= 0 || !_prototypes.HasIndex<StackPrototype>(stackType))
                return false;
        }

        var consumed = new List<(ProtoId<StackPrototype> StackType, int Amount)>();
        foreach (var entry in consumption)
        {
            var before = entry.Stack.Count;
            var used = !TerminatingOrDeleted(refineryUid) &&
                       !TerminatingOrDeleted(entry.Uid) &&
                       _stack.TryUse((entry.Uid, entry.Stack), entry.Amount);
            var consumedExactly = used &&
                                  !TerminatingOrDeleted(entry.Uid) &&
                                  entry.Stack.Count == before - entry.Amount;
            if (!consumedExactly)
            {
                if (used)
                {
                    if (TerminatingOrDeleted(entry.Uid))
                        _stack.SpawnMultipleAtPosition(entry.Stack.StackTypeId, before, coordinates);
                    else
                        _stack.SetCount((entry.Uid, entry.Stack), before);
                }

                foreach (var rollback in consumed)
                    _stack.SpawnMultipleAtPosition(rollback.StackType, rollback.Amount, coordinates);
                return false;
            }

            consumed.Add((entry.Stack.StackTypeId, entry.Amount));
        }

        if (TerminatingOrDeleted(refineryUid))
        {
            foreach (var rollback in consumed)
                _stack.SpawnMultipleAtPosition(rollback.StackType, rollback.Amount, coordinates);
            return false;
        }

        refinery.Jobs.Add(new FrontlineRefineryJob
        {
            Recipe = recipeId,
            Remaining = recipe.Duration,
        });
        _active.Add(refineryUid);
        return true;
    }

    private bool IsCompletionEntityAlive(EntityUid uid) =>
        !TerminatingOrDeleted(uid) && !EntityManager.IsQueuedForDeletion(uid);

    private bool TryCompleteJob(EntityUid uid,
        FrontlineRefineryComponent refinery,
        FrontlineRefineryJob job,
        FrontlineRefineryRecipePrototype recipe)
    {
        if (!_completing.Add(uid))
            return false;

        var outputs = new Dictionary<EntityUid, (ProtoId<StackPrototype> Type, int Count)>();
        var completed = false;
        try
        {
            if (!IsCompletionEntityAlive(uid) ||
                !_containers.TryGetContainer(uid, FrontlineRefineryComponent.OutputContainerId, out var container) ||
                container != refinery.OutputContainer)
                return false;

            foreach (var (stackType, amount) in recipe.Output)
            {
                var prototype = _prototypes.Index(stackType);
                var remaining = amount;
                while (remaining > 0)
                {
                    if (!IsCompletionEntityAlive(uid))
                        return false;

                    // Spawn in nullspace and track before eventful count/insertion APIs.
                    // The entity manager cleans up initialization failures inside Spawn.
                    var output = Spawn(prototype.Spawn);
                    outputs.Add(output, (stackType, 0));
                    if (!IsCompletionEntityAlive(output) ||
                        !TryComp<StackComponent>(output, out var stack) ||
                        stack.Unlimited || stack.StackTypeId != stackType)
                        return false;

                    var maxCount = _stack.GetMaxCount(stack);
                    if (maxCount <= 0)
                        return false;

                    var count = Math.Min(remaining, maxCount);
                    outputs[output] = (stackType, count);
                    _stack.SetCount((output, stack), count);
                    if (!IsCompletionEntityAlive(uid) || !IsCompletionEntityAlive(output) ||
                        !_containers.Insert(output, container))
                        return false;

                    remaining -= count;
                }
            }

            // Later callbacks can invalidate any earlier staged stack, not just the last one.
            if (!IsCompletionEntityAlive(uid) ||
                !TryComp<FrontlineRefineryComponent>(uid, out var current) || current != refinery ||
                !_containers.TryGetContainer(uid, FrontlineRefineryComponent.OutputContainerId, out var retained) ||
                retained != container ||
                outputs.Any(entry => !IsCompletionEntityAlive(entry.Key) ||
                    !retained.Contains(entry.Key) ||
                    !TryComp<StackComponent>(entry.Key, out var stack) || stack.Unlimited ||
                    stack.StackTypeId != entry.Value.Type || stack.Count != entry.Value.Count))
                return false;

            completed = refinery.Jobs.Remove(job);
            return completed;
        }
        finally
        {
            try
            {
                if (!completed)
                {
                    // Queue all first so a deletion callback exception cannot orphan later outputs.
                    foreach (var output in outputs.Keys)
                    {
                        if (TerminatingOrDeleted(output))
                            continue;

                        try
                        {
                            QueueDel(output);
                        }
                        catch (Exception e)
                        {
                            // QueueDeleteEntity queues before notifying callbacks; finish the rest.
                            Log.Error($"Exception queuing refinery output cleanup for {output}: {e}");
                        }
                    }
                    foreach (var output in outputs.Keys)
                    {
                        if (!TerminatingOrDeleted(output))
                            Del(output);
                    }
                }
            }
            finally
            {
                _completing.Remove(uid);
            }
        }
    }

    private Dictionary<string, Entity<FrontlineRefineryComponent>> GetRefineries(MapId mapId)
    {
        var result = new Dictionary<string, Entity<FrontlineRefineryComponent>>();
        var query = AllEntityQuery<FrontlineRefineryComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var refinery, out var transform))
        {
            if (transform.MapID != mapId)
                continue;
            if (!IsCompletionEntityAlive(uid) || string.IsNullOrWhiteSpace(refinery.RefineryId) ||
                !result.TryAdd(refinery.RefineryId, (uid, refinery)) ||
                _submitting.Contains(uid) || _completing.Contains(uid) || _taking.Contains(uid) || _movingInputs.Contains(uid) ||
                !_containers.TryGetContainer(uid, FrontlineRefineryComponent.InputContainerId, out var input) ||
                input != refinery.InputContainer ||
                !_containers.TryGetContainer(uid, FrontlineRefineryComponent.OutputContainerId, out var output) ||
                output != refinery.OutputContainer)
                throw new InvalidDataException("Refinery identity, containers or economic operation is unsettled.");
        }
        return result;
    }

    private List<WarRefineryStackSnapshot> CaptureStacks(BaseContainer container)
    {
        var result = new List<WarRefineryStackSnapshot>();
        var seen = new HashSet<EntityUid>();
        foreach (var uid in container.ContainedEntities)
        {
            if (!seen.Add(uid) || !IsCompletionEntityAlive(uid) ||
                !_containers.TryGetContainingContainer(uid, out var owner) || owner != container ||
                !TryComp<StackComponent>(uid, out var stack) || stack.Unlimited)
                throw new InvalidDataException("Refinery contains an invalid or unsettled physical claim.");
            var entry = new WarRefineryStackSnapshot(stack.StackTypeId.Id,
                MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Refinery stack has no prototype."),
                stack.Count);
            ValidateStack(entry);
            result.Add(entry);
        }
        return result;
    }

    public List<WarRefinerySnapshot> CaptureSnapshot(MapId mapId)
    {
        var result = new List<WarRefinerySnapshot>();
        foreach (var (id, entity) in GetRefineries(mapId))
        {
            if (_restoring.Contains(entity.Owner))
                throw new InvalidDataException("Refinery restoration must settle before capture.");
            result.Add(new WarRefinerySnapshot(id,
                MetaData(entity.Owner).EntityPrototype?.ID ?? throw new InvalidDataException("Refinery has no prototype."),
                entity.Comp.Jobs.Select(job => new WarRefineryJobSnapshot(job.Recipe.Id, job.Remaining.Ticks)).ToList(),
                CaptureStacks(entity.Comp.InputContainer), CaptureStacks(entity.Comp.OutputContainer)));
        }
        ValidateSnapshot(mapId, result);
        return result;
    }

    private void ValidateStack(WarRefineryStackSnapshot entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.StackId) || string.IsNullOrWhiteSpace(entry.Prototype) ||
            !IsSpawnableStack(new ProtoId<StackPrototype>(entry.StackId)) ||
            !_prototypes.TryIndex(new EntProtoId(entry.Prototype), out var prototype) || prototype.Abstract ||
            !prototype.TryGetComponent<StackComponent>(out var stack, _componentFactory) ||
            stack.Unlimited || stack.StackTypeId.Id != entry.StackId || entry.Count <= 0 ||
            entry.Count > _stack.GetMaxCount(stack))
            throw new InvalidDataException("Invalid refinery stack prototype, type or count.");
    }

    internal void ValidateSnapshot(MapId mapId, List<WarRefinerySnapshot> entries)
    {
        var refineries = GetRefineries(mapId);
        if (entries.Count != refineries.Count)
            throw new InvalidDataException("Refinery snapshot must cover every map refinery.");
        var seen = new HashSet<string>();
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.RefineryId) || !seen.Add(entry.RefineryId) ||
                !refineries.TryGetValue(entry.RefineryId, out var entity) ||
                string.IsNullOrWhiteSpace(entry.Prototype) || MetaData(entity.Owner).EntityPrototype?.ID != entry.Prototype ||
                !_prototypes.TryIndex(new EntProtoId(entry.Prototype), out var prototype) || prototype.Abstract ||
                !prototype.HasComp<FrontlineRefineryComponent>(_componentFactory) ||
                entry.Jobs == null || entry.Inputs == null || entry.Outputs == null)
                throw new InvalidDataException("Invalid refinery identity, prototype or claims.");
            foreach (var job in entry.Jobs)
            {
                if (job == null || string.IsNullOrWhiteSpace(job.Recipe) ||
                    !_prototypes.TryIndex(new ProtoId<FrontlineRefineryRecipePrototype>(job.Recipe), out var recipe) ||
                    !IsValidRecipe(recipe) || job.RemainingTicks < 0 || job.RemainingTicks > recipe.Duration.Ticks)
                    throw new InvalidDataException("Invalid paid refinery recipe or remaining time.");
            }
            foreach (var stack in entry.Inputs.Concat(entry.Outputs))
                ValidateStack(stack);
        }
    }

    internal void StageSnapshot(MapId mapId, List<WarRefinerySnapshot> entries, List<EntityUid> staged,
        List<EntityUid> restoring)
    {
        var refineries = GetRefineries(mapId);
        // v1 restores onto clean mapper machines only, never overwrites another paid ledger.
        foreach (var entity in refineries.Values)
        {
            if (entity.Comp.Jobs.Count != 0 || entity.Comp.InputContainer.ContainedEntities.Count != 0 ||
                entity.Comp.OutputContainer.ContainedEntities.Count != 0 || !_restoring.Add(entity.Owner))
                throw new InvalidDataException("Refinery restore requires a clean mapper machine.");
            restoring.Add(entity.Owner);
        }
        foreach (var entry in entries)
        {
            var entity = refineries[entry.RefineryId];
            StageStacks(entity, entity.Comp.InputContainer, entry.Inputs);
            StageStacks(entity, entity.Comp.OutputContainer, entry.Outputs);
        }
        ValidateStagedSnapshot(mapId, entries, staged);

        void StageStacks(Entity<FrontlineRefineryComponent> entity, BaseContainer container,
            List<WarRefineryStackSnapshot> stacks)
        {
            foreach (var entry in stacks)
            {
                if (!IsCompletionEntityAlive(entity.Owner))
                    throw new InvalidDataException("Refinery was lost during staging.");
                var uid = Spawn(new EntProtoId(entry.Prototype));
                staged.Add(uid); // Track before eventful SetCount and Insert, as for completion.
                if (!IsCompletionEntityAlive(uid) || !TryComp<StackComponent>(uid, out var stack) ||
                    stack.Unlimited || stack.StackTypeId.Id != entry.StackId)
                    throw new InvalidDataException("Restored refinery stack did not initialize correctly.");
                _stack.SetCount((uid, stack), entry.Count);
                if (!IsCompletionEntityAlive(entity.Owner) || !IsCompletionEntityAlive(uid) ||
                    !_containers.Insert(uid, container) || !IsCompletionEntityAlive(uid) ||
                    !container.Contains(uid) || stack.Count != entry.Count || stack.StackTypeId.Id != entry.StackId)
                    throw new InvalidDataException("Restored refinery stack was not retained exactly.");
            }
        }
    }

    internal void ValidateStagedSnapshot(MapId mapId, List<WarRefinerySnapshot> entries,
        List<EntityUid> staged, bool committed = false)
    {
        ValidateSnapshot(mapId, entries);
        var refineries = GetRefineries(mapId);
        if (staged.Count != entries.Sum(entry => entry.Inputs.Count + entry.Outputs.Count) ||
            staged.Distinct().Count() != staged.Count)
            throw new InvalidDataException("Restored refinery physical claims changed.");
        var index = 0;
        foreach (var entry in entries)
        {
            var entity = refineries[entry.RefineryId];
            if (!_restoring.Contains(entity.Owner) ||
                (committed ? !entity.Comp.Jobs.Select(job => new WarRefineryJobSnapshot(job.Recipe.Id, job.Remaining.Ticks))
                    .SequenceEqual(entry.Jobs) : entity.Comp.Jobs.Count != 0))
                throw new InvalidDataException("Restored refinery queue changed.");
            CheckStacks(entity.Comp.InputContainer, entry.Inputs);
            CheckStacks(entity.Comp.OutputContainer, entry.Outputs);
        }

        void CheckStacks(BaseContainer container, List<WarRefineryStackSnapshot> stacks)
        {
            var expected = staged.Skip(index).Take(stacks.Count).ToArray();
            if (!container.ContainedEntities.SequenceEqual(expected) ||
                !CaptureStacks(container).SequenceEqual(stacks))
                throw new InvalidDataException("Restored refinery contents changed during native callbacks.");
            index += stacks.Count;
        }
    }

    internal void CommitSnapshot(MapId mapId, List<WarRefinerySnapshot> entries, List<EntityUid> staged)
    {
        ValidateStagedSnapshot(mapId, entries, staged);
        var refineries = GetRefineries(mapId);
        foreach (var entry in entries)
        {
            var entity = refineries[entry.RefineryId];
            entity.Comp.Jobs = entry.Jobs.Select(job => new FrontlineRefineryJob
            {
                Recipe = new ProtoId<FrontlineRefineryRecipePrototype>(job.Recipe),
                Remaining = TimeSpan.FromTicks(job.RemainingTicks),
            }).ToList();
            if (entity.Comp.Jobs.Count > 0)
                _active.Add(entity.Owner); // Restore is after MapInit; resume through native Update only.
        }
        ValidateStagedSnapshot(mapId, entries, staged, committed: true);
    }

    internal void DeleteStagedStacks(List<EntityUid> staged)
    {
        // Queue every claim before eventful deletion; cleanup exceptions still abort map acceptance.
        foreach (var uid in staged)
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }
        foreach (var uid in staged)
        {
            if (!TerminatingOrDeleted(uid))
                Del(uid);
        }
    }

    internal void FinishSnapshotRestore(List<EntityUid> restoring)
    {
        foreach (var uid in restoring)
            _restoring.Remove(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _uiUpdateAccumulator += frameTime;
        var updateUi = _uiUpdateAccumulator >= 1f;
        if (updateUi)
            _uiUpdateAccumulator = 0f;

        foreach (var uid in _active.ToArray())
        {
            if (!TryComp<FrontlineRefineryComponent>(uid, out var refinery) || TerminatingOrDeleted(uid))
            {
                _active.Remove(uid);
                continue;
            }

            if (MetaData(uid).EntityPaused || _completing.Contains(uid) || _restoring.Contains(uid))
                continue;

            var jobsBefore = refinery.Jobs.Count;

            // Invalid paid jobs stay quarantined in the ledger; capture/restore refuses them.
            var processing = refinery.Jobs
                .Where(job => !string.IsNullOrWhiteSpace(job.Recipe.Id) &&
                    _prototypes.TryIndex(job.Recipe, out var recipe) && IsValidRecipe(recipe))
                .Take(Math.Max(1, refinery.ProcessingSlots))
                .Reverse().ToArray();
            foreach (var job in processing)
            {
                if (string.IsNullOrWhiteSpace(job.Recipe.Id) ||
                    !_prototypes.TryIndex(job.Recipe, out var recipe) || !IsValidRecipe(recipe))
                    continue;

                job.Remaining = TimeSpan.FromTicks(Math.Max(0,
                    job.Remaining.Ticks - TimeSpan.FromSeconds(frameTime).Ticks));
                if (job.Remaining > TimeSpan.Zero)
                    continue;

                TryCompleteJob(uid, refinery, job, recipe);
                if (!IsCompletionEntityAlive(uid))
                    break;
            }

            if (!IsCompletionEntityAlive(uid))
                continue;

            if (refinery.Jobs.Count == 0)
                _active.Remove(uid);

            if (updateUi || refinery.Jobs.Count != jobsBefore)
                UpdateUi((uid, refinery));
        }
    }
}
