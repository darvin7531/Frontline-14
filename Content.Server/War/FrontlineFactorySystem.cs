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
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.Localization;
using Robust.Shared.Map;
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
    [Dependency] private IPlayerManager _players = default!;

    [Dependency] private FrontlineProductionSystem _production = default!;
    private readonly Dictionary<(EntityUid Machine, EntityUid Actor), bool> _modes = new();
    private readonly HashSet<EntityUid> _active = new();
    private readonly HashSet<EntityUid> _submitting = new();
    private readonly HashSet<EntityUid> _completing = new();
    private readonly HashSet<EntityUid> _takingOutput = new();
    private readonly HashSet<EntityUid> _movingInputs = new();
    private readonly Dictionary<EntityUid, (string Id, FrontlineFactoryComponent Component, Container Input, Container Output)> _restoring = new();
    private float _uiUpdateAccumulator;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineFactoryComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<FrontlineFactoryComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<FrontlineFactoryComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<FrontlineFactoryComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<FrontlineFactoryComponent, BeforeActivatableUIOpenEvent>(OnBeforeUiOpen);
        SubscribeLocalEvent<FrontlineFactoryComponent, FrontlineFactorySubmitMessage>(OnSubmitMessage);
        SubscribeLocalEvent<FrontlineFactoryComponent, FrontlineFactoryModeMessage>(OnModeMessage);
        SubscribeLocalEvent<FrontlineFactoryComponent, FrontlineFactoryCancelMessage>(OnCancelMessage);
        SubscribeLocalEvent<FrontlineFactoryComponent, FrontlineFactoryEjectMessage>(OnEjectMessage);
        SubscribeLocalEvent<FrontlineFactoryComponent, FrontlineFactoryTakeOutputMessage>(OnTakeOutputMessage);
        SubscribeLocalEvent<FrontlineFactoryComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<FrontlineFactoryComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
    }

    private void OnStartup(Entity<FrontlineFactoryComponent> factory, ref ComponentStartup args)
    {
        factory.Comp.InputContainer = _containers.EnsureContainer<Container>(
            factory,
            FrontlineFactoryComponent.InputContainerId);
        factory.Comp.OutputContainer = _containers.EnsureContainer<Container>(
            factory,
            FrontlineFactoryComponent.OutputContainerId);
    }

    private void OnMapInit(Entity<FrontlineFactoryComponent> factory, ref MapInitEvent args)
    {
        if (factory.Comp.Jobs.Count > 0)
            _active.Add(factory);
    }

    private void OnTerminating(Entity<FrontlineFactoryComponent> factory, ref EntityTerminatingEvent args)
    {
        _active.Remove(factory);
        foreach (var key in _modes.Keys.Where(key => key.Machine == factory.Owner).ToArray())
            _modes.Remove(key);
        if (_production.Flushing) return;
        // Native technical flush removes all entities after persistence has captured them.
        // Actual destruction consumes private ready goods, never spills them into public logistics.
        foreach (var output in factory.Comp.OutputContainer.ContainedEntities.ToArray())
        {
            if (TryComp<FrontlineProductionClaimComponent>(output, out var claim) &&
                claim.IsPersonal(DateTimeOffset.UtcNow) && !TerminatingOrDeleted(output))
                QueueDel(output);
        }
        factory.Comp.Jobs.Clear();
    }

    private void OnInteractUsing(Entity<FrontlineFactoryComponent> factory, ref InteractUsingEvent args)
    {
        if (args.Handled || _restoring.ContainsKey(factory.Owner) ||
            !_interaction.InRangeUnobstructed(args.User, factory.Owner))
            return;

        if (!CanAcceptInput(args.Used))
        {
            _popup.PopupEntity(Loc.GetString("frontline-factory-invalid-input"), factory.Owner, args.User);
            return;
        }

        if (TryInsertPlayerInput(factory.Owner, args.User, args.Used))
            args.Handled = true;
    }

    private void OnBeforeUiOpen(Entity<FrontlineFactoryComponent> factory, ref BeforeActivatableUIOpenEvent args)
    {
        UpdateUi(factory);
    }

    private void OnModeMessage(Entity<FrontlineFactoryComponent> factory, ref FrontlineFactoryModeMessage args)
    {
        if (!_players.TryGetSessionByEntity(args.Actor, out _) ||
            !_interaction.InRangeUnobstructed(args.Actor, factory.Owner)) return;
        _modes[(factory.Owner, args.Actor)] = args.Personal;
        UpdateUi(factory);
    }

    private void OnCancelMessage(Entity<FrontlineFactoryComponent> factory, ref FrontlineFactoryCancelMessage args)
    {
        TryCancelPlayerJob(factory.Owner, args.Actor, args.Id, args.Personal);
        UpdateUi(factory);
    }

    public bool TryCancelPlayerJob(EntityUid uid, EntityUid actor, Guid id, bool personal = false)
    {
        if (!IsOutputEntityAlive(uid) || !IsOutputEntityAlive(actor) ||
            !TryComp<FrontlineFactoryComponent>(uid, out var component) ||
            !_players.TryGetSessionByEntity(actor, out var session) ||
            _restoring.ContainsKey(uid) || _submitting.Contains(uid) ||
            _completing.Contains(uid) || _takingOutput.Contains(uid) || !_movingInputs.Add(uid)) return false;
        try
        {
            var owner = personal ? session.UserId.ToString() : null;
            var job = component.Jobs.FirstOrDefault(job => job.Id == id);
            if (job == null || job.Legacy || job.PaidInputs.Count == 0 ||
                (owner == null ? job.Access != FrontlineProductionAccess.Public :
                 job.Access != FrontlineProductionAccess.Personal || job.Owner != owner)) return false;
            var container = component.InputContainer;
            var receipt = new Dictionary<ProtoId<StackPrototype>, int>(job.PaidInputs);
            bool Authorized() => IsOutputEntityAlive(uid) && IsOutputEntityAlive(actor) &&
                _players.TryGetSessionByEntity(actor, out var currentSession) && currentSession == session &&
                _interaction.InRangeUnobstructed(actor, uid) &&
                TryComp<FrontlineFactoryComponent>(uid, out var current) && current == component &&
                current.InputContainer == container && current.Jobs.Contains(job) && !job.Legacy &&
                job.Id == id && job.Owner == owner &&
                (owner == null ? job.Access == FrontlineProductionAccess.Public : job.Access == FrontlineProductionAccess.Personal) &&
                job.PaidInputs.Count == receipt.Count && receipt.All(entry => job.PaidInputs.GetValueOrDefault(entry.Key) == entry.Value);
            return _production.TryRefund(container, receipt, 1,
                Authorized, () => component.Jobs.Remove(job));
        }
        finally { _movingInputs.Remove(uid); }
    }

    private void OnSubmitMessage(Entity<FrontlineFactoryComponent> factory, ref FrontlineFactorySubmitMessage args)
    {
        if (!TrySubmitPlayerJob(factory.Owner, args.Actor, args.Recipe, args.Personal))
            _popup.PopupEntity(Loc.GetString("frontline-factory-insufficient-input"), factory.Owner, args.Actor);

        UpdateUi(factory);
    }

    private void OnEjectMessage(Entity<FrontlineFactoryComponent> factory, ref FrontlineFactoryEjectMessage args)
    {
        if (TryEjectPlayerInputs(factory.Owner, args.Actor))
            UpdateUi(factory);
    }

    private void OnTakeOutputMessage(Entity<FrontlineFactoryComponent> factory, ref FrontlineFactoryTakeOutputMessage args)
    {
        TryTakePlayerOutput(factory.Owner, args.Actor, args.Personal);
    }

    public bool TryTakePlayerOutput(EntityUid factoryUid, EntityUid playerUid, bool personal = false)
    {
        if (!IsOutputEntityAlive(factoryUid) || !IsOutputEntityAlive(playerUid) ||
            !TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) ||
            _restoring.ContainsKey(factoryUid) || _completing.Contains(factoryUid) || !_takingOutput.Add(factoryUid))
            return false;

        if (!_players.TryGetSessionByEntity(playerUid, out var session))
        {
            _takingOutput.Remove(factoryUid);
            return false;
        }
        var owner = personal ? session.UserId.ToString() : null;
        bool Authorized() => _players.TryGetSessionByEntity(playerUid, out var currentSession) &&
            currentSession == session && (owner == null || currentSession.UserId.ToString() == owner);
        EntityUid? output = null;
        var taken = false;
        try
        {
            if (!_interaction.InRangeUnobstructed(playerUid, factoryUid) ||
                !_containers.TryGetContainer(factoryUid, FrontlineFactoryComponent.OutputContainerId, out var container) ||
                container != factory.OutputContainer || container.ContainedEntities.Count == 0)
                return false;

            foreach (var candidate in container.ContainedEntities)
            {
                if (IsOutputEntityAlive(candidate) && _production.CanAccess(candidate, owner))
                { output = candidate; break; }
            }
            if (output == null || !Authorized()) return false;
            var hand = _hands.GetActiveHand(playerUid);
            if (hand == null || !_hands.ActiveHandIsEmpty(playerUid) ||
                !IsOutputEntityAlive(output.Value) ||
                !_hands.CanPickupToHand(playerUid, output.Value, hand))
                return false;

            // Permission events may change the actor, hand or retained output.
            if (!IsOutputEntityAlive(factoryUid) || !IsOutputEntityAlive(playerUid) ||
                !IsOutputEntityAlive(output.Value) || !Authorized() || !_production.CanAccess(output.Value, owner) ||
                !_interaction.InRangeUnobstructed(playerUid, factoryUid) ||
                _hands.GetActiveHand(playerUid) != hand || !_hands.ActiveHandIsEmpty(playerUid) ||
                !_containers.TryGetContainer(factoryUid, FrontlineFactoryComponent.OutputContainerId, out var current) ||
                current != container || !container.Contains(output.Value))
                return false;

            _hands.TryPickup(playerUid, output.Value, hand);
            // TryPickup can return true even when DoPickup did not insert the item.
            taken = Authorized() && _production.CanAccess(output.Value, owner) && IsOutputEntityAlive(factoryUid) && IsOutputEntityAlive(playerUid) &&
                    IsOutputEntityAlive(output.Value) &&
                    _interaction.InRangeUnobstructed(playerUid, factoryUid) &&
                    _hands.GetActiveHand(playerUid) == hand &&
                    _hands.GetActiveItem(playerUid) == output.Value &&
                    _containers.TryGetContainer(factoryUid, FrontlineFactoryComponent.OutputContainerId, out current) &&
                    current == container && !container.Contains(output.Value);
            if (taken && output is { } withdrawn)
                RemComp<FrontlineProductionClaimComponent>(withdrawn);
            return taken;
        }
        finally
        {
            try
            {
                if (!taken && output is { } retained && IsOutputEntityAlive(retained) &&
                    !TerminatingOrDeleted(factoryUid) &&
                    _containers.TryGetContainer(factoryUid, FrontlineFactoryComponent.OutputContainerId, out var container) &&
                    container == factory.OutputContainer && !container.Contains(retained))
                {
                    // Roll back an eventful failed pickup, never eject the output to the floor.
                    if (_containers.TryGetContainingContainer((retained, null, null), out var previous))
                        _containers.Remove(retained, previous, reparent: false, force: true);
                    if (!TerminatingOrDeleted(factoryUid) && IsOutputEntityAlive(retained) &&
                        (!_containers.Insert(retained, container, force: true) || !container.Contains(retained)))
                        Log.Error($"Failed to retain factory output {ToPrettyString(retained)} in {ToPrettyString(factoryUid)}.");
                }
            }
            finally
            {
                _takingOutput.Remove(factoryUid);
                UpdateUi((factoryUid, factory));
            }
        }
    }

    private void OnContainerChanged(Entity<FrontlineFactoryComponent> factory, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID is FrontlineFactoryComponent.InputContainerId or FrontlineFactoryComponent.OutputContainerId)
            UpdateUi(factory);
    }

    private void OnContainerChanged(Entity<FrontlineFactoryComponent> factory, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID is FrontlineFactoryComponent.InputContainerId or FrontlineFactoryComponent.OutputContainerId)
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
                Id = job.Id,
                Owner = job.Owner,
                Access = job.Access,
                Legacy = job.Legacy,
                PaidInputs = new(job.PaidInputs),
            }).ToArray()
            : Array.Empty<FrontlineFactoryJob>();

    public FrontlineFactoryUiState BuildUiState(EntityUid factoryUid, string? owner = null)
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
        var jobs = factory.Jobs
            .Where(job => owner == null ? job.Access == FrontlineProductionAccess.Public :
                job.Access == FrontlineProductionAccess.Personal && job.Owner == owner)
            .Where(job => _prototypes.TryIndex(job.Recipe, out FrontlineFactoryRecipePrototype? recipe) && IsValidRecipe(recipe))
            .Select((job, index) => new FrontlineFactoryJobState(job.Recipe, job.Remaining, index < 5, job.Id, !job.Legacy && job.PaidInputs.Count > 0))
            .ToArray();

        var outputs = factory.OutputContainer.ContainedEntities
            .Where(uid => !TerminatingOrDeleted(uid) && _production.CanAccess(uid, owner))
            .Select(uid => MetaData(uid).EntityPrototype?.ID)
            .Where(id => id != null)
            .GroupBy(id => id!)
            .Select(group => new FrontlineFactoryOutputState(new EntProtoId(group.Key), group.Count()))
            .ToArray();

        return new FrontlineFactoryUiState(inputs, recipes, jobs, outputs);
    }

    private void UpdateUi(Entity<FrontlineFactoryComponent> factory)
    {
        if (TerminatingOrDeleted(factory) || _restoring.ContainsKey(factory.Owner) ||
            _completing.Contains(factory) || _takingOutput.Contains(factory))
            return;

        _ui.SetUiState(factory.Owner, FrontlineFactoryUiKey.Key, BuildUiState(factory.Owner));
        var actors = _ui.GetActors(factory.Owner, FrontlineFactoryUiKey.Key).ToArray();
        foreach (var actor in actors)
        {
            if (!_players.TryGetSessionByEntity(actor, out var session)) continue;
            var personal = _modes.GetValueOrDefault((factory.Owner, actor));
            _ui.ServerSendUiMessage(factory.Owner, FrontlineFactoryUiKey.Key,
                new FrontlineFactoryViewMessage(BuildUiState(factory.Owner, personal ? session.UserId.ToString() : null), personal), actor);
        }
        foreach (var key in _modes.Keys.Where(key => key.Machine == factory.Owner && !actors.Contains(key.Actor)).ToArray())
            _modes.Remove(key);
    }

    private bool IsValidRecipe(FrontlineFactoryRecipePrototype recipe)
    {
        return recipe.Duration > TimeSpan.Zero &&
               recipe.Input.Count > 0 &&
               recipe.OutputAmount > 0 &&
               recipe.Input.All(entry => entry.Value > 0 && IsSpawnableStack(entry.Key)) &&
               HasValidLocalization(recipe.Name) &&
               !string.IsNullOrWhiteSpace(recipe.Output.Id) &&
               _prototypes.TryIndex<EntityPrototype>(recipe.Output, out var output) &&
               !output.Abstract;
    }

    internal bool HasValidLocalization(LocId name)
    {
        return _localization.HasString(name);
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

    public bool TryInsertInput(EntityUid factoryUid, EntityUid inputUid)
    {
        if (!TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) ||
            _restoring.ContainsKey(factoryUid) || !IsOutputEntityAlive(factoryUid) ||
            !IsOutputEntityAlive(inputUid) || !CanAcceptInput(inputUid) || !_movingInputs.Add(factoryUid))
            return false;

        try
        {
            return _containers.Insert(inputUid, factory.InputContainer);
        }
        finally
        {
            _movingInputs.Remove(factoryUid);
        }
    }

    public bool TryInsertPlayerInput(EntityUid factoryUid, EntityUid playerUid, EntityUid inputUid)
    {
        if (!TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) ||
            _restoring.ContainsKey(factoryUid) || !IsOutputEntityAlive(factoryUid) ||
            !IsOutputEntityAlive(playerUid) || !IsOutputEntityAlive(inputUid) ||
            !CanAcceptInput(inputUid) || !_movingInputs.Add(factoryUid))
            return false;

        try
        {
            return _interaction.InRangeUnobstructed(playerUid, factoryUid) &&
                   _hands.TryDropIntoContainer(playerUid, inputUid, factory.InputContainer);
        }
        finally
        {
            _movingInputs.Remove(factoryUid);
        }
    }

    private bool CanAcceptInput(EntityUid inputUid)
    {
        return TryComp<StackComponent>(inputUid, out var stack) &&
               !stack.Unlimited &&
               GetAvailableRecipes().Any(recipe => recipe.Input.ContainsKey(stack.StackTypeId));
    }

    public void EjectInputs(EntityUid factoryUid)
    {
        if (_restoring.ContainsKey(factoryUid) || !IsOutputEntityAlive(factoryUid) ||
            !TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) || !_movingInputs.Add(factoryUid))
            return;

        try
        {
            _containers.EmptyContainer(factory.InputContainer);
        }
        finally
        {
            _movingInputs.Remove(factoryUid);
        }
    }

    public bool TryEjectPlayerInputs(EntityUid factoryUid, EntityUid playerUid)
    {
        if (_restoring.ContainsKey(factoryUid) || _movingInputs.Contains(factoryUid) ||
            !IsOutputEntityAlive(factoryUid) || !IsOutputEntityAlive(playerUid) ||
            !_interaction.InRangeUnobstructed(playerUid, factoryUid))
            return false;

        EjectInputs(factoryUid);
        return true;
    }

    public bool TrySubmitContainedJob(EntityUid factoryUid, ProtoId<FrontlineFactoryRecipePrototype> recipeId,
        string? owner = null)
    {
        return TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) &&
               TrySubmitJob(factoryUid, recipeId, factory.InputContainer.ContainedEntities.ToArray(), owner);
    }

    public bool TrySubmitPlayerJob(EntityUid factoryUid,
        EntityUid playerUid,
        ProtoId<FrontlineFactoryRecipePrototype> recipeId,
        bool personal = false)
    {
        if (!IsOutputEntityAlive(playerUid) || !IsOutputEntityAlive(factoryUid) ||
            !_players.TryGetSessionByEntity(playerUid, out var session) ||
            !TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) ||
            !_interaction.InRangeUnobstructed(playerUid, factoryUid))
            return false;

        var container = factory.InputContainer;
        return TrySubmitJob(factoryUid, recipeId, container.ContainedEntities.ToArray(),
            personal ? session.UserId.ToString() : null,
            () => IsOutputEntityAlive(playerUid) && IsOutputEntityAlive(factoryUid) &&
                _players.TryGetSessionByEntity(playerUid, out var currentSession) && currentSession == session &&
                _interaction.InRangeUnobstructed(playerUid, factoryUid) &&
                TryComp<FrontlineFactoryComponent>(factoryUid, out var current) && current == factory &&
                current.InputContainer == container);
    }

    /// <summary>
    /// The caller must provide authorized physical inputs. Player-facing callers must not pass
    /// arbitrary client-selected entity IDs without server-side authorization.
    /// </summary>
    public bool TrySubmitJob(
        EntityUid factoryUid,
        ProtoId<FrontlineFactoryRecipePrototype> recipeId,
        IReadOnlyCollection<EntityUid> inputs,
        string? owner = null, Func<bool>? authorized = null)
    {
        if (!TryComp<FrontlineFactoryComponent>(factoryUid, out var factory) ||
            !IsOutputEntityAlive(factoryUid) || _restoring.ContainsKey(factoryUid) ||
            _movingInputs.Contains(factoryUid) || _completing.Contains(factoryUid) || _takingOutput.Contains(factoryUid) ||
            string.IsNullOrWhiteSpace(recipeId.Id) || !_prototypes.TryIndex(recipeId, out var recipe) ||
            !IsValidRecipe(recipe) ||
            !_submitting.Add(factoryUid))
            return false;

        try
        {
            return TrySubmitValidated(factoryUid, factory, recipeId, recipe, inputs, owner, authorized);
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
        IReadOnlyCollection<EntityUid> inputs,
        string? owner, Func<bool>? authorized)
    {
        var coordinates = Transform(factoryUid).Coordinates;
        var paidInputs = recipe.Input.ToDictionary(entry => entry.Key, entry => entry.Value);
        if ((owner != null && !FrontlineProductionSystem.ValidOwner(owner)) ||
            factory.Jobs.Count >= 4096 || !_production.RefundFits(paidInputs, 1)) return false;

        var stacks = new Dictionary<EntityUid, StackComponent>();
        foreach (var uid in inputs)
        {
            if (!stacks.ContainsKey(uid) &&
                TryComp<StackComponent>(uid, out var stack) &&
                !stack.Unlimited && stack.Count > 0 &&
                IsOutputEntityAlive(uid))
                stacks.Add(uid, stack);
        }

        var consumption = new List<(EntityUid Uid, StackComponent Stack, int Amount)>();
        foreach (var (stackType, required) in paidInputs)
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
            var used = IsOutputEntityAlive(factoryUid) && authorized?.Invoke() != false &&
                       IsOutputEntityAlive(entry.Uid) &&
                       (authorized == null || factory.InputContainer.Contains(entry.Uid)) &&
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

        if (!IsOutputEntityAlive(factoryUid) || authorized?.Invoke() == false)
        {
            foreach (var rollback in consumed)
                _stack.SpawnMultipleAtPosition(rollback.StackType, rollback.Amount, coordinates);
            return false;
        }

        factory.Jobs.Add(new FrontlineFactoryJob
        {
            Recipe = recipeId,
            Remaining = recipe.Duration,
            Access = owner == null ? FrontlineProductionAccess.Public : FrontlineProductionAccess.Personal,
            Owner = owner,
            PaidInputs = paidInputs,
        });
        _active.Add(factoryUid);
        return true;
    }

    private bool IsOutputEntityAlive(EntityUid uid) =>
        !TerminatingOrDeleted(uid) && !EntityManager.IsQueuedForDeletion(uid);

    private bool TryCompleteJob(EntityUid uid,
        FrontlineFactoryComponent factory,
        FrontlineFactoryJob job,
        FrontlineFactoryRecipePrototype recipe)
    {
        var outputs = new EntityUid?[recipe.OutputAmount];
        if (_restoring.ContainsKey(uid) || _takingOutput.Contains(uid) || !_completing.Add(uid))
            return false;

        var completed = false;
        try
        {
            for (var i = 0; i < outputs.Length; i++)
            {
                if (!IsOutputEntityAlive(uid) ||
                    !TrySpawnInContainer(recipe.Output, uid, FrontlineFactoryComponent.OutputContainerId, out outputs[i]))
                    return false;
            }

            if (!IsOutputEntityAlive(uid) ||
                !_containers.TryGetContainer(uid, FrontlineFactoryComponent.OutputContainerId, out var container) ||
                container != factory.OutputContainer ||
                outputs.Any(output => output == null || !IsOutputEntityAlive(output.Value) || !container.Contains(output.Value)))
                return false;

            var completedAt = DateTimeOffset.UtcNow;
            foreach (var output in outputs)
                if (output is { } item) _production.CompleteClaim(item, job.Owner, completedAt);
            completed = factory.Jobs.Remove(job);
            return completed;
        }
        finally
        {
            try
            {
                if (!completed)
                {
                    // TrySpawnInContainer leaves its out UID assigned if insertion throws.
                    // Spawn failures themselves are cleaned up by the entity manager.
                    // Queue all first so a deletion callback exception cannot orphan later outputs.
                    foreach (var output in outputs)
                    {
                        if (output == null || TerminatingOrDeleted(output.Value))
                            continue;

                        try
                        {
                            QueueDel(output.Value);
                        }
                        catch (Exception e)
                        {
                            // QueueDeleteEntity queues before notifying callbacks; finish the rest.
                            Log.Error($"Exception queuing factory output cleanup for {output.Value}: {e}");
                        }
                    }
                    foreach (var output in outputs)
                    {
                        if (output != null && !TerminatingOrDeleted(output.Value))
                            Del(output.Value);
                    }
                }
            }
            finally
            {
                _completing.Remove(uid);
                UpdateUi((uid, factory));
            }
        }
    }

    private Dictionary<string, Entity<FrontlineFactoryComponent>> GetFactories(MapId mapId)
    {
        var result = new Dictionary<string, Entity<FrontlineFactoryComponent>>();
        var query = AllEntityQuery<FrontlineFactoryComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var factory, out var transform))
        {
            if (transform.MapID != mapId)
                continue;
            if (!IsOutputEntityAlive(uid) || string.IsNullOrWhiteSpace(factory.FactoryId) ||
                !result.TryAdd(factory.FactoryId, (uid, factory)) ||
                _submitting.Contains(uid) || _completing.Contains(uid) || _takingOutput.Contains(uid) || _movingInputs.Contains(uid) ||
                !_containers.TryGetContainer(uid, FrontlineFactoryComponent.InputContainerId, out var input) ||
                input != factory.InputContainer ||
                !_containers.TryGetContainer(uid, FrontlineFactoryComponent.OutputContainerId, out var output) ||
                output != factory.OutputContainer ||
                (_restoring.TryGetValue(uid, out var original) &&
                 (original.Id != factory.FactoryId || original.Component != factory ||
                  original.Input != input || original.Output != output)))
                throw new InvalidDataException("Factory identity, containers or economic operation is unsettled.");
        }
        return result;
    }

    private List<WarRefineryStackSnapshot> CaptureStacks(BaseContainer container)
    {
        var result = new List<WarRefineryStackSnapshot>();
        var seen = new HashSet<EntityUid>();
        foreach (var uid in container.ContainedEntities)
        {
            if (!seen.Add(uid) || !IsOutputEntityAlive(uid) ||
                !_containers.TryGetContainingContainer(uid, out var owner) || owner != container ||
                !TryComp<StackComponent>(uid, out var stack) || stack.Unlimited)
                throw new InvalidDataException("Factory contains an invalid or unsettled physical claim.");
            var entry = new WarRefineryStackSnapshot(stack.StackTypeId.Id,
                MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Factory stack has no prototype."),
                stack.Count, _production.CaptureClaim(uid));
            ValidateStack(entry);
            result.Add(entry);
        }
        return result;
    }

    private List<WarFactoryCrateSnapshot> CaptureCrates(BaseContainer container)
    {
        var result = new List<WarFactoryCrateSnapshot>();
        var seen = new HashSet<EntityUid>();
        foreach (var uid in container.ContainedEntities)
        {
            if (!seen.Add(uid) || !IsOutputEntityAlive(uid) ||
                !_containers.TryGetContainingContainer(uid, out var owner) || owner != container ||
                HasComp<ContainerManagerComponent>(uid) || HasComp<Content.Shared.Storage.StorageComponent>(uid) ||
                HasComp<Content.Shared.Storage.Components.EntityStorageComponent>(uid) ||
                !TryComp<FrontlineSupplyCrateComponent>(uid, out var crate))
                throw new InvalidDataException("Factory contains an invalid or unsettled sealed claim.");
            var entry = new WarFactoryCrateSnapshot(
                MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Factory crate has no prototype."),
                crate.Product.Id, crate.Amount, _production.CaptureClaim(uid));
            ValidateCrate(entry);
            result.Add(entry);
        }
        return result;
    }

    internal void ValidateCrate(WarFactoryCrateSnapshot entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Prototype) || string.IsNullOrWhiteSpace(entry.Product) ||
            entry.Amount <= 0 || !_prototypes.TryIndex(new EntProtoId(entry.Prototype), out var prototype) ||
            prototype.Abstract || !prototype.HasComp<FrontlineSupplyCrateComponent>(_componentFactory) ||
            prototype.HasComp<ContainerManagerComponent>(_componentFactory) ||
            prototype.HasComp<Content.Shared.Storage.StorageComponent>(_componentFactory) ||
            prototype.HasComp<Content.Shared.Storage.Components.EntityStorageComponent>(_componentFactory) ||
            !_prototypes.TryIndex(new ProtoId<FrontlineSupplyProductPrototype>(entry.Product), out var product) ||
            !HasValidLocalization(product.Name) ||
            (product.Entity is { } goods && (string.IsNullOrWhiteSpace(goods.Id) ||
                !_prototypes.TryIndex(goods, out var entity) || entity.Abstract)))
            throw new InvalidDataException("Invalid factory sealed crate prototype, product or amount.");
        FrontlineProductionSystem.ValidateClaim(entry.Claim);
    }

    // Factory-owned configuration: restore the actual sealed entitlement, not a recipe/default profile.
    internal void RestoreCrate(EntityUid uid, WarFactoryCrateSnapshot entry)
    {
        if (!IsOutputEntityAlive(uid) || !TryComp<FrontlineSupplyCrateComponent>(uid, out var crate))
            throw new InvalidDataException("Restored factory crate did not initialize correctly.");
        crate.Product = new ProtoId<FrontlineSupplyProductPrototype>(entry.Product);
        crate.Amount = entry.Amount;
        _production.RestoreClaim(uid, entry.Claim);
        if (!IsOutputEntityAlive(uid) || MetaData(uid).EntityPrototype?.ID != entry.Prototype ||
            !TryComp<FrontlineSupplyCrateComponent>(uid, out var current) || current != crate ||
            current.Product.Id != entry.Product || current.Amount != entry.Amount ||
            HasComp<ContainerManagerComponent>(uid) || HasComp<Content.Shared.Storage.StorageComponent>(uid) ||
            HasComp<Content.Shared.Storage.Components.EntityStorageComponent>(uid))
            throw new InvalidDataException("Restored factory crate did not retain its configured entitlement.");
    }

    public List<WarFactorySnapshot> CaptureSnapshot(MapId mapId)
    {
        var result = new List<WarFactorySnapshot>();
        foreach (var (id, entity) in GetFactories(mapId))
        {
            if (_restoring.ContainsKey(entity.Owner))
                throw new InvalidDataException("Factory restoration must settle before capture.");
            result.Add(new WarFactorySnapshot(id,
                MetaData(entity.Owner).EntityPrototype?.ID ?? throw new InvalidDataException("Factory has no prototype."),
                entity.Comp.Jobs.Select(job => new WarFactoryJobSnapshot(job.Recipe.Id, job.Remaining.Ticks, _production.CaptureJob(job.Id, job.Owner, job.Legacy || job.PaidInputs.Count == 0, job.PaidInputs))).ToList(),
                CaptureStacks(entity.Comp.InputContainer), CaptureCrates(entity.Comp.OutputContainer)));
        }
        ValidateSnapshot(mapId, result);
        return result;
    }

    internal void ValidateStack(WarRefineryStackSnapshot entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.StackId) || string.IsNullOrWhiteSpace(entry.Prototype) ||
            !IsSpawnableStack(new ProtoId<StackPrototype>(entry.StackId)) ||
            !_prototypes.TryIndex(new EntProtoId(entry.Prototype), out var prototype) || prototype.Abstract ||
            !prototype.TryGetComponent<StackComponent>(out var stack, _componentFactory) ||
            stack.Unlimited || stack.StackTypeId.Id != entry.StackId || entry.Count <= 0 ||
            entry.Count > _stack.GetMaxCount(stack))
            throw new InvalidDataException("Invalid factory stack prototype, type or count.");
        FrontlineProductionSystem.ValidateClaim(entry.Claim);
    }

    internal void ValidateSnapshot(MapId mapId, List<WarFactorySnapshot> entries)
    {
        var factories = GetFactories(mapId);
        if (entries.Count != factories.Count)
            throw new InvalidDataException("Factory snapshot must cover every map factory.");
        var seen = new HashSet<string>();
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.FactoryId) || !seen.Add(entry.FactoryId) ||
                !factories.TryGetValue(entry.FactoryId, out var entity) ||
                string.IsNullOrWhiteSpace(entry.Prototype) || MetaData(entity.Owner).EntityPrototype?.ID != entry.Prototype ||
                !_prototypes.TryIndex(new EntProtoId(entry.Prototype), out var prototype) || prototype.Abstract ||
                !prototype.HasComp<FrontlineFactoryComponent>(_componentFactory) ||
                entry.Jobs == null || entry.Inputs == null || entry.Outputs == null)
                throw new InvalidDataException("Invalid factory identity, prototype or claims.");
            var jobIds = new HashSet<Guid>();
            foreach (var job in entry.Jobs)
            {
                if (job == null || string.IsNullOrWhiteSpace(job.Recipe) ||
                    !_prototypes.TryIndex(new ProtoId<FrontlineFactoryRecipePrototype>(job.Recipe), out var recipe) ||
                    !IsValidRecipe(recipe) || job.RemainingTicks < 0 || job.RemainingTicks > recipe.Duration.Ticks)
                    throw new InvalidDataException("Invalid paid factory recipe or remaining time.");
                if (job.Claim == null || !jobIds.Add(job.Claim.Id))
                    throw new InvalidDataException("Missing or duplicate production job claim.");
                _production.ValidateJob(job.Claim);
            }
            foreach (var stack in entry.Inputs)
                ValidateStack(stack);
            foreach (var crate in entry.Outputs)
                ValidateCrate(crate);
        }
    }

    internal void StageSnapshot(MapId mapId, List<WarFactorySnapshot> entries, List<EntityUid> staged,
        List<EntityUid> restoring)
    {
        var factories = GetFactories(mapId);
        ValidateSnapshot(mapId, entries);
        // Preflight ALL machines before acquiring guards or staging any paid claim.
        foreach (var entity in factories.Values)
        {
            if (entity.Comp.Jobs.Count != 0 || entity.Comp.InputContainer.ContainedEntities.Count != 0 ||
                entity.Comp.OutputContainer.ContainedEntities.Count != 0 || _restoring.ContainsKey(entity.Owner))
                throw new InvalidDataException("Factory restore requires a clean mapper machine.");
        }
        foreach (var entity in factories.Values)
        {
            _restoring.Add(entity.Owner, (entity.Comp.FactoryId, entity.Comp, entity.Comp.InputContainer, entity.Comp.OutputContainer));
            restoring.Add(entity.Owner);
        }
        foreach (var entry in entries)
        {
            var entity = factories[entry.FactoryId];
            StageStacks(entity, entity.Comp.InputContainer, entry.Inputs);
            foreach (var crate in entry.Outputs)
            {
                if (!IsOutputEntityAlive(entity.Owner))
                    throw new InvalidDataException("Factory was lost during staging.");
                ValidateStagedSnapshot(mapId, entries, staged, partial: true);
                var uid = Spawn(new EntProtoId(crate.Prototype));
                staged.Add(uid); // Record before metadata configuration or eventful insertion.
                RestoreCrate(uid, crate);
                ValidateStagedSnapshot(mapId, entries, staged.Take(staged.Count - 1).ToList(), partial: true);
                if (!IsOutputEntityAlive(entity.Owner) || !IsOutputEntityAlive(uid) ||
                    !_containers.Insert(uid, entity.Comp.OutputContainer))
                    throw new InvalidDataException("Restored factory crate was not retained.");
                ValidateStagedSnapshot(mapId, entries, staged, partial: true);
            }
        }
        ValidateStagedSnapshot(mapId, entries, staged);

        void StageStacks(Entity<FrontlineFactoryComponent> entity, BaseContainer container,
            List<WarRefineryStackSnapshot> stacks)
        {
            foreach (var entry in stacks)
            {
                if (!IsOutputEntityAlive(entity.Owner))
                    throw new InvalidDataException("Factory was lost during staging.");
                ValidateStagedSnapshot(mapId, entries, staged, partial: true);
                var uid = Spawn(new EntProtoId(entry.Prototype));
                staged.Add(uid); // Track before eventful SetCount and Insert, as for completion.
                if (!IsOutputEntityAlive(uid) || !TryComp<StackComponent>(uid, out var stack) ||
                    stack.Unlimited || stack.StackTypeId.Id != entry.StackId)
                    throw new InvalidDataException("Restored factory stack did not initialize correctly.");
                _production.RestoreClaim(uid, entry.Claim);
                _stack.SetCount((uid, stack), entry.Count);
                ValidateStagedSnapshot(mapId, entries, staged.Take(staged.Count - 1).ToList(), partial: true);
                if (!IsOutputEntityAlive(entity.Owner) || !IsOutputEntityAlive(uid) ||
                    !_containers.Insert(uid, container) || !IsOutputEntityAlive(uid) ||
                    !container.Contains(uid) || stack.Count != entry.Count || stack.StackTypeId.Id != entry.StackId)
                    throw new InvalidDataException("Restored factory stack was not retained exactly.");
                ValidateStagedSnapshot(mapId, entries, staged, partial: true);
            }
        }
    }

    internal void ValidateStagedSnapshot(MapId mapId, List<WarFactorySnapshot> entries,
        List<EntityUid> staged, bool committed = false, bool partial = false)
    {
        ValidateSnapshot(mapId, entries);
        var factories = GetFactories(mapId);
        // ponytail: whole-ledger readback per callback is quadratic; cap claim counts if maps grow large.
        var expectedCount = entries.Sum(entry => entry.Inputs.Count + entry.Outputs.Count);
        if ((partial ? staged.Count > expectedCount : staged.Count != expectedCount) ||
            staged.Distinct().Count() != staged.Count)
            throw new InvalidDataException("Restored factory physical claims changed.");
        var index = 0;
        foreach (var entry in entries)
        {
            var entity = factories[entry.FactoryId];
            if (!_restoring.ContainsKey(entity.Owner) ||
                (committed ? !entity.Comp.Jobs.Select(job => new WarFactoryJobSnapshot(job.Recipe.Id, job.Remaining.Ticks, _production.CaptureJob(job.Id, job.Owner, job.Legacy || job.PaidInputs.Count == 0, job.PaidInputs)))
                    .SequenceEqual(entry.Jobs) : entity.Comp.Jobs.Count != 0))
                throw new InvalidDataException("Restored factory queue changed.");
            CheckStacks(entity.Comp.InputContainer, entry.Inputs);
            var expected = staged.Skip(index).Take(entry.Outputs.Count).ToArray();
            if (!entity.Comp.OutputContainer.ContainedEntities.SequenceEqual(expected) ||
                !CaptureCrates(entity.Comp.OutputContainer).SequenceEqual(entry.Outputs.Take(expected.Length)))
                throw new InvalidDataException("Restored factory crates changed during native callbacks.");
            index += entry.Outputs.Count;
        }

        void CheckStacks(BaseContainer container, List<WarRefineryStackSnapshot> stacks)
        {
            var expected = staged.Skip(index).Take(stacks.Count).ToArray();
            if (!container.ContainedEntities.SequenceEqual(expected) ||
                !CaptureStacks(container).SequenceEqual(stacks.Take(expected.Length)))
                throw new InvalidDataException("Restored factory contents changed during native callbacks.");
            index += stacks.Count;
        }
    }

    internal void CommitSnapshot(MapId mapId, List<WarFactorySnapshot> entries, List<EntityUid> staged)
    {
        ValidateStagedSnapshot(mapId, entries, staged);
        var factories = GetFactories(mapId);
        foreach (var entry in entries)
        {
            var entity = factories[entry.FactoryId];
            entity.Comp.Jobs = entry.Jobs.Select(job => new FrontlineFactoryJob
            {
                Recipe = new ProtoId<FrontlineFactoryRecipePrototype>(job.Recipe),
                Remaining = TimeSpan.FromTicks(job.RemainingTicks),
                Id = job.Claim!.Id,
                Owner = job.Claim.Owner,
                Access = job.Claim.Owner == null ? FrontlineProductionAccess.Public : FrontlineProductionAccess.Personal,
                Legacy = job.Claim.Legacy,
                PaidInputs = job.Claim.PaidInputs.ToDictionary(payment => new ProtoId<StackPrototype>(payment.StackId), payment => payment.Amount),
            }).ToList();
            if (entity.Comp.Jobs.Count > 0)
                _active.Add(entity.Owner); // Restore is after MapInit; resume through native Update only.
        }
        ValidateStagedSnapshot(mapId, entries, staged, committed: true);
    }

    internal void DeleteStagedClaims(List<EntityUid> staged)
    {
        // Queue every claim before eventful deletion; cleanup exceptions still abort map acceptance.
        Exception? queueFailure = null;
        foreach (var uid in staged)
        {
            if (TerminatingOrDeleted(uid))
                continue;

            try
            {
                QueueDel(uid);
            }
            catch (Exception e)
            {
                // Native QueueDeleteEntity queues before notifying; quarantine the remaining claims too.
                queueFailure ??= e;
            }
        }
        if (queueFailure != null)
            throw queueFailure;
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
        {
            _uiUpdateAccumulator = 0f;
            // Ready goods expire by UTC even when the queue is empty or the map is paused.
            var query = EntityQueryEnumerator<FrontlineFactoryComponent>();
            while (query.MoveNext(out var machine, out var component))
                if (_ui.GetActors(machine, FrontlineFactoryUiKey.Key).Any())
                    UpdateUi((machine, component));
        }

        foreach (var uid in _active.ToArray())
        {
            if (!TryComp<FrontlineFactoryComponent>(uid, out var factory) || TerminatingOrDeleted(uid))
            {
                _active.Remove(uid);
                continue;
            }

            if (MetaData(uid).EntityPaused || _restoring.ContainsKey(uid) ||
                _submitting.Contains(uid) || _movingInputs.Contains(uid) ||
                _completing.Contains(uid) || _takingOutput.Contains(uid))
                continue;

            var jobsBefore = factory.Jobs.Count;

            // Public has five slots, and every authenticated personal owner has five independent slots.
            var processing = factory.Jobs
                .Where(job => !string.IsNullOrWhiteSpace(job.Recipe.Id) &&
                    _prototypes.TryIndex(job.Recipe, out var recipe) && IsValidRecipe(recipe))
                .GroupBy(job => job.Access == FrontlineProductionAccess.Personal && !string.IsNullOrWhiteSpace(job.Owner)
                    ? job.Owner!
                    : string.Empty)
                .SelectMany(group => group.Take(5))
                .Reverse().ToArray();
            foreach (var job in processing)
            {
                if (!_prototypes.TryIndex(job.Recipe, out var recipe) || !IsValidRecipe(recipe))
                    continue;

                job.Remaining = TimeSpan.FromTicks(Math.Max(0,
                    job.Remaining.Ticks - TimeSpan.FromSeconds(frameTime).Ticks));
                if (job.Remaining > TimeSpan.Zero)
                    continue;

                TryCompleteJob(uid, factory, job, recipe);
                if (TerminatingOrDeleted(uid))
                    break;
            }

            if (TerminatingOrDeleted(uid))
                continue;

            if (factory.Jobs.Count == 0)
                _active.Remove(uid);

            if (updateUi || factory.Jobs.Count != jobsBefore)
                UpdateUi((uid, factory));
        }
    }
}
