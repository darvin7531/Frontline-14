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
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class FrontlineFactorySystem : EntitySystem
{
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ILocalizationManager _localization = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly HashSet<EntityUid> _active = new();
    private readonly HashSet<EntityUid> _submitting = new();
    private float _uiUpdateAccumulator;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineFactoryComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<FrontlineFactoryComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<FrontlineFactoryComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<FrontlineFactoryComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<FrontlineFactoryComponent, BeforeActivatableUIOpenEvent>(OnBeforeUiOpen);
        SubscribeLocalEvent<FrontlineFactoryComponent, FrontlineFactorySubmitMessage>(OnSubmitMessage);
        SubscribeLocalEvent<FrontlineFactoryComponent, FrontlineFactoryEjectMessage>(OnEjectMessage);
        SubscribeLocalEvent<FrontlineFactoryComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<FrontlineFactoryComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
    }

    private void OnStartup(Entity<FrontlineFactoryComponent> factory, ref ComponentStartup args)
    {
        factory.Comp.InputContainer = _containers.EnsureContainer<Container>(
            factory,
            FrontlineFactoryComponent.InputContainerId);
    }

    private void OnMapInit(Entity<FrontlineFactoryComponent> factory, ref MapInitEvent args)
    {
        if (factory.Comp.Jobs.Count > 0)
            _active.Add(factory);
    }

    private void OnTerminating(Entity<FrontlineFactoryComponent> factory, ref EntityTerminatingEvent args)
    {
        _active.Remove(factory);
    }

    private void OnInteractUsing(Entity<FrontlineFactoryComponent> factory, ref InteractUsingEvent args)
    {
        if (args.Handled || !_interaction.InRangeUnobstructed(args.User, factory.Owner))
            return;

        if (!CanAcceptInput(args.Used))
        {
            _popup.PopupEntity(Loc.GetString("frontline-factory-invalid-input"), factory.Owner, args.User);
            return;
        }

        if (_hands.TryDropIntoContainer(args.User, args.Used, factory.Comp.InputContainer))
            args.Handled = true;
    }

    private void OnBeforeUiOpen(Entity<FrontlineFactoryComponent> factory, ref BeforeActivatableUIOpenEvent args)
    {
        UpdateUi(factory);
    }

    private void OnSubmitMessage(Entity<FrontlineFactoryComponent> factory, ref FrontlineFactorySubmitMessage args)
    {
        if (!TrySubmitPlayerJob(factory.Owner, args.Actor, args.Recipe))
            _popup.PopupEntity(Loc.GetString("frontline-factory-insufficient-input"), factory.Owner, args.Actor);

        UpdateUi(factory);
    }

    private void OnEjectMessage(Entity<FrontlineFactoryComponent> factory, ref FrontlineFactoryEjectMessage args)
    {
        if (TryEjectPlayerInputs(factory.Owner, args.Actor))
            UpdateUi(factory);
    }

    private void OnContainerChanged(Entity<FrontlineFactoryComponent> factory, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == FrontlineFactoryComponent.InputContainerId)
            UpdateUi(factory);
    }

    private void OnContainerChanged(Entity<FrontlineFactoryComponent> factory, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == FrontlineFactoryComponent.InputContainerId)
            UpdateUi(factory);
    }

    public IEnumerable<FrontlineFactoryRecipePrototype> GetAvailableRecipes() =>
        _prototypes.EnumeratePrototypes<FrontlineFactoryRecipePrototype>().Where(IsValidRecipe);

    public IReadOnlyList<FrontlineFactoryJob> GetJobs(EntityUid factoryUid) =>
        TryComp<FrontlineFactoryComponent>(factoryUid, out var factory)
            ? factory.Jobs.Select(job => new FrontlineFactoryJob
            {
                Recipe = job.Recipe,
                Remaining = job.Remaining,
            }).ToArray()
            : Array.Empty<FrontlineFactoryJob>();

    public FrontlineFactoryUiState BuildUiState(EntityUid factoryUid)
    {
        if (!TryComp<FrontlineFactoryComponent>(factoryUid, out var factory))
            return new FrontlineFactoryUiState([], [], []);

        var amounts = new Dictionary<ProtoId<StackPrototype>, int>();
        foreach (var uid in factory.InputContainer.ContainedEntities)
        {
            if (!TryComp<StackComponent>(uid, out var stack) || stack.Unlimited)
                continue;

            amounts[stack.StackTypeId] = amounts.GetValueOrDefault(stack.StackTypeId) + stack.Count;
        }

        var inputs = amounts
            .Select(entry => new FrontlineFactoryInputState(entry.Key, entry.Value))
            .ToArray();
        var recipes = GetAvailableRecipes()
            .Select(recipe =>
            {
                return new FrontlineFactoryRecipeState(
                    recipe.ID,
                    recipe.Input.Select(entry => new FrontlineFactoryInputState(entry.Key, entry.Value)).ToArray(),
                    recipe.Output,
                    recipe.OutputAmount,
                    recipe.Duration,
                    recipe.Input.All(entry => amounts.GetValueOrDefault(entry.Key) >= entry.Value));
            })
            .ToArray();
        var processing = Math.Max(1, factory.ProcessingSlots);
        var jobs = factory.Jobs
            .Where(job => _prototypes.TryIndex(job.Recipe, out FrontlineFactoryRecipePrototype? recipe) && IsValidRecipe(recipe))
            .Select((job, index) => new FrontlineFactoryJobState(job.Recipe, job.Remaining, index < processing))
            .ToArray();

        return new FrontlineFactoryUiState(inputs, recipes, jobs);
    }

    private void UpdateUi(Entity<FrontlineFactoryComponent> factory)
    {
        _ui.SetUiState(factory.Owner, FrontlineFactoryUiKey.Key, BuildUiState(factory.Owner));
    }

    private bool IsValidRecipe(FrontlineFactoryRecipePrototype recipe)
    {
        return recipe.Duration > TimeSpan.Zero &&
               recipe.Input.Count > 0 &&
               recipe.OutputAmount > 0 &&
               recipe.Input.All(entry => entry.Value > 0 && IsSpawnableStack(entry.Key)) &&
               _localization.HasString(recipe.Name) &&
               !string.IsNullOrWhiteSpace(recipe.Output.Id) &&
               _prototypes.TryIndex<EntityPrototype>(recipe.Output, out var output) &&
               !output.Abstract;
    }

    private bool IsSpawnableStack(ProtoId<StackPrototype> stackId)
    {
        return _prototypes.TryIndex(stackId, out var stack) &&
               !stack.Abstract &&
               !string.IsNullOrWhiteSpace(stack.Spawn.Id) &&
               _prototypes.TryIndex<EntityPrototype>(stack.Spawn, out var spawn) &&
               spawn.HasComp<StackComponent>(_componentFactory);
    }

    public bool TryInsertInput(EntityUid factoryUid, EntityUid inputUid)
    {
        if (!TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) ||
            TerminatingOrDeleted(factoryUid) ||
            TerminatingOrDeleted(inputUid) ||
            !CanAcceptInput(inputUid))
            return false;

        return _containers.Insert(inputUid, factory.InputContainer);
    }

    public bool TryInsertPlayerInput(EntityUid factoryUid, EntityUid playerUid, EntityUid inputUid)
    {
        return TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) &&
               _interaction.InRangeUnobstructed(playerUid, factoryUid) &&
               CanAcceptInput(inputUid) &&
               _hands.TryDropIntoContainer(playerUid, inputUid, factory.InputContainer);
    }

    private bool CanAcceptInput(EntityUid inputUid)
    {
        return TryComp<StackComponent>(inputUid, out var stack) &&
               !stack.Unlimited &&
               GetAvailableRecipes().Any(recipe => recipe.Input.ContainsKey(stack.StackTypeId));
    }

    public void EjectInputs(EntityUid factoryUid)
    {
        if (!TryComp<FrontlineFactoryComponent>(factoryUid, out var factory))
            return;

        _containers.EmptyContainer(factory.InputContainer);
    }

    public bool TryEjectPlayerInputs(EntityUid factoryUid, EntityUid playerUid)
    {
        if (!_interaction.InRangeUnobstructed(playerUid, factoryUid))
            return false;

        EjectInputs(factoryUid);
        return true;
    }

    public bool TrySubmitContainedJob(EntityUid factoryUid, ProtoId<FrontlineFactoryRecipePrototype> recipeId)
    {
        return TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) &&
               TrySubmitJob(factoryUid, recipeId, factory.InputContainer.ContainedEntities.ToArray());
    }

    public bool TrySubmitPlayerJob(EntityUid factoryUid,
        EntityUid playerUid,
        ProtoId<FrontlineFactoryRecipePrototype> recipeId)
    {
        return _interaction.InRangeUnobstructed(playerUid, factoryUid) &&
               TrySubmitContainedJob(factoryUid, recipeId);
    }

    /// <summary>
    /// The caller must provide authorized physical inputs. Player-facing callers must not pass
    /// arbitrary client-selected entity IDs without server-side authorization.
    /// </summary>
    public bool TrySubmitJob(
        EntityUid factoryUid,
        ProtoId<FrontlineFactoryRecipePrototype> recipeId,
        IReadOnlyCollection<EntityUid> inputs)
    {
        if (!TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) ||
            TerminatingOrDeleted(factoryUid) ||
            !_prototypes.TryIndex(recipeId, out var recipe) ||
            !IsValidRecipe(recipe) ||
            !_submitting.Add(factoryUid))
            return false;

        try
        {
            return TrySubmitValidated(factoryUid, factory, recipeId, recipe, inputs);
        }
        finally
        {
            _submitting.Remove(factoryUid);
        }
    }

    private bool TrySubmitValidated(
        EntityUid factoryUid,
        FrontlineFactoryComponent factory,
        ProtoId<FrontlineFactoryRecipePrototype> recipeId,
        FrontlineFactoryRecipePrototype recipe,
        IReadOnlyCollection<EntityUid> inputs)
    {
        var coordinates = Transform(factoryUid).Coordinates;

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

        var consumed = new List<(ProtoId<StackPrototype> StackType, int Amount)>();
        foreach (var entry in consumption)
        {
            var before = entry.Stack.Count;
            var used = !TerminatingOrDeleted(factoryUid) &&
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

        if (TerminatingOrDeleted(factoryUid))
        {
            foreach (var rollback in consumed)
                _stack.SpawnMultipleAtPosition(rollback.StackType, rollback.Amount, coordinates);
            return false;
        }

        factory.Jobs.Add(new FrontlineFactoryJob
        {
            Recipe = recipeId,
            Remaining = recipe.Duration,
        });
        _active.Add(factoryUid);
        return true;
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
            if (!TryComp<FrontlineFactoryComponent>(uid, out var factory) || TerminatingOrDeleted(uid))
            {
                _active.Remove(uid);
                continue;
            }

            if (MetaData(uid).EntityPaused)
                continue;

            var jobsBefore = factory.Jobs.Count;

            for (var i = factory.Jobs.Count - 1; i >= 0; i--)
            {
                var job = factory.Jobs[i];
                if (!_prototypes.TryIndex(job.Recipe, out var recipe) || !IsValidRecipe(recipe))
                    factory.Jobs.RemoveAt(i);
            }

            var processing = Math.Min(Math.Max(1, factory.ProcessingSlots), factory.Jobs.Count);
            for (var i = processing - 1; i >= 0; i--)
            {
                var job = factory.Jobs[i];
                if (!_prototypes.TryIndex(job.Recipe, out var recipe) || !IsValidRecipe(recipe))
                    continue;

                job.Remaining -= TimeSpan.FromSeconds(frameTime);
                if (job.Remaining > TimeSpan.Zero)
                    continue;

                factory.Jobs.RemoveAt(i);
                for (var output = 0; output < recipe.OutputAmount; output++)
                    Spawn(recipe.Output, Transform(uid).Coordinates);
            }

            if (factory.Jobs.Count == 0)
                _active.Remove(uid);

            if (updateUi || factory.Jobs.Count != jobsBefore)
                UpdateUi((uid, factory));
        }
    }
}
