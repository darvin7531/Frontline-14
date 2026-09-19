using System.Linq;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class FrontlineRefinerySystem : EntitySystem
{
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private StackSystem _stack = default!;

    private readonly HashSet<EntityUid> _active = new();
    private readonly HashSet<EntityUid> _submitting = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineRefineryComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<FrontlineRefineryComponent, EntityTerminatingEvent>(OnTerminating);
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
        return _prototypes.TryIndex(stackId, out var stack) &&
               !stack.Abstract &&
               !string.IsNullOrWhiteSpace(stack.Spawn.Id) &&
               _prototypes.TryIndex<EntityPrototype>(stack.Spawn, out var spawn) &&
               spawn.HasComp<StackComponent>(_componentFactory);
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

    public override void Update(float frameTime)
    {
        foreach (var uid in _active.ToArray())
        {
            if (!TryComp<FrontlineRefineryComponent>(uid, out var refinery) || TerminatingOrDeleted(uid))
            {
                _active.Remove(uid);
                continue;
            }

            if (MetaData(uid).EntityPaused)
                continue;

            for (var i = refinery.Jobs.Count - 1; i >= 0; i--)
            {
                var job = refinery.Jobs[i];
                if (!_prototypes.TryIndex(job.Recipe, out var recipe) || !IsValidRecipe(recipe))
                    continue;

                job.Remaining -= TimeSpan.FromSeconds(frameTime);
                if (job.Remaining > TimeSpan.Zero)
                    continue;

                refinery.Jobs.RemoveAt(i);
                foreach (var (stackType, amount) in recipe.Output)
                {
                    foreach (var output in _stack.SpawnMultipleAtPosition(stackType, amount, Transform(uid).Coordinates))
                        _stack.TryMergeToContacts(output);
                }
            }

            if (refinery.Jobs.Count == 0)
                _active.Remove(uid);
        }
    }
}
