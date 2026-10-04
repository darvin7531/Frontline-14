using System.Linq;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.War;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class FrontlineStockpileSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ILocalizationManager _localization = default!;

    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    private static readonly ProtoId<StackPrototype> MaterialsStack = "BasicMaterials";
    private static readonly ProtoId<FrontlineSupplyProductPrototype> MaterialsProduct = "BasicMaterials";
    private static readonly ProtoId<FrontlineSupplyProductPrototype> SoldierSupplies = "SoldierSupplies";

    private readonly HashSet<EntityUid> _operating = new();

    private bool TryInput(EntityUid held, out ProtoId<FrontlineSupplyProductPrototype> product, out int amount)
    {
        product = default;
        amount = 0;
        if (TryComp<StackComponent>(held, out var stack))
        {
            if (stack.StackTypeId != MaterialsStack || stack.Unlimited)
                return false;
            product = MaterialsProduct;
            amount = stack.Count;
        }
        else if (TryComp<FrontlineSupplyCrateComponent>(held, out var crate))
        {
            product = crate.Product;
            amount = crate.Amount;
        }
        return amount > 0 && TryProduct(product, out _);
    }

    private bool CanAccess(EntityUid core, EntityUid actor) =>
        !TerminatingOrDeleted(core) && !TerminatingOrDeleted(actor) &&
        HasComp<FrontlineStockpileComponent>(core) &&
        _interaction.InRangeUnobstructed(actor, core);

    private bool TryProduct(ProtoId<FrontlineSupplyProductPrototype> id,
        out FrontlineSupplyProductPrototype product)
    {
        product = default!;
        if (string.IsNullOrWhiteSpace(id.Id) || !_prototypes.TryIndex(id, out var found))
            return false;
        product = found;
        return _localization.HasString(product.Name) &&
               (product.Entity == null ||
                (!string.IsNullOrWhiteSpace(product.Entity.Value.Id) &&
                 _prototypes.TryIndex(product.Entity.Value, out var entity) && !entity.Abstract));
    }

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineStockpileComponent, BeforeActivatableUIOpenEvent>(OnOpen);
        SubscribeLocalEvent<FrontlineStockpileComponent, FrontlineStockpileSubmitMessage>(OnSubmit);
        SubscribeLocalEvent<FrontlineStockpileComponent, FrontlineStockpileWithdrawMessage>(OnWithdraw);
    }

    private void OnOpen(Entity<FrontlineStockpileComponent> core, ref BeforeActivatableUIOpenEvent args) => UpdateUi(core);

    private void OnSubmit(Entity<FrontlineStockpileComponent> core, ref FrontlineStockpileSubmitMessage args)
    {
        TrySubmitHeld(core, args.Actor);
        UpdateUi(core);
    }

    private void OnWithdraw(Entity<FrontlineStockpileComponent> core, ref FrontlineStockpileWithdrawMessage args)
    {
        TryWithdrawPlayerProduct(core, args.Actor, args.Product);
        UpdateUi(core);
    }

    private void UpdateUi(EntityUid core)
    {
        if (!TerminatingOrDeleted(core))
            _ui.SetUiState(core, FrontlineStockpileUiKey.Key, BuildUiState(core));
    }

    public FrontlineStockpileUiState BuildUiState(EntityUid core)
    {
        if (TerminatingOrDeleted(core) || !TryComp<FrontlineStockpileComponent>(core, out var stockpile))
            return new([]);
        return new(_prototypes.EnumeratePrototypes<FrontlineSupplyProductPrototype>()
            .Where(product => TryProduct(product.ID, out _))
            .OrderBy(product => product.ID)
            .Select(product => new FrontlineStockpileProductState(product.ID,
                stockpile.Counts.GetValueOrDefault(product.ID),
                product.Entity != null && stockpile.Counts.GetValueOrDefault(product.ID) > 0))
            .ToArray());
    }

    /// <summary>Trusted server operation; true commits the spawn and its one-supply charge.</summary>
    public bool TrySpendSoldierSupply(EntityUid core, Func<bool> operation)
    {
        if (TerminatingOrDeleted(core) || EntityManager.IsQueuedForDeletion(core) ||
            !TryComp<FrontlineStockpileComponent>(core, out var stockpile) || !_operating.Add(core))
            return false;

        var committed = false;
        var reserved = false;
        var before = stockpile.Counts.GetValueOrDefault(SoldierSupplies);
        try
        {
            if (before <= 0)
                return false;
            stockpile.Counts[SoldierSupplies] = before - 1;
            reserved = true;
            committed = operation();
            return committed;
        }
        finally
        {
            if (reserved && !committed)
                stockpile.Counts[SoldierSupplies] = before;
            _operating.Remove(core);
            // UI publication is not part of the paid spawn commit and cannot undo it.
            try
            {
                UpdateUi(core);
            }
            catch (Exception e)
            {
                Log.Error($"Failed to refresh soldier supply stockpile {core}: {e}");
            }
        }
    }

    public bool TrySubmitHeld(EntityUid core, EntityUid actor)
    {
        if (!CanAccess(core, actor) || !_operating.Add(core))
            return false;

        try
        {
            if (!_hands.TryGetActiveItem(actor, out var held) || TerminatingOrDeleted(held) ||
                !TryInput(held.Value, out var product, out var amount) ||
                !_hands.CanDrop(actor, held.Value) || !CanAccess(core, actor) ||
                TerminatingOrDeleted(held) || _hands.GetActiveItem(actor) != held ||
                !TryInput(held.Value, out product, out amount))
                return false;

            var stockpile = Comp<FrontlineStockpileComponent>(core);
            var before = stockpile.Counts.GetValueOrDefault(product);
            if (before < 0 || amount > int.MaxValue - before)
                return false;

            // Reserve before synchronous termination callbacks; nested operations cannot reuse the input.
            stockpile.Counts[product] = before + amount;
            try
            {
                Del(held.Value);
            }
            catch
            {
                if (!TerminatingOrDeleted(core) && !TerminatingOrDeleted(held))
                    stockpile.Counts[product] = before;
                throw;
            }
            return true;
        }
        finally
        {
            _operating.Remove(core);
            UpdateUi(core);
        }
    }

    public bool TryWithdrawPlayerProduct(EntityUid core, EntityUid actor,
        ProtoId<FrontlineSupplyProductPrototype> product)
    {
        if (!CanAccess(core, actor) || !TryProduct(product, out var definition) ||
            definition.Entity == null || !_operating.Add(core))
            return false;

        try
        {
            var stockpile = Comp<FrontlineStockpileComponent>(core);
            var before = stockpile.Counts.GetValueOrDefault(product);
            if (before <= 0)
                return false;

            var amount = product == MaterialsProduct ? Math.Min(before, 30) : 1;
            stockpile.Counts[product] = before - amount;
            EntityUid? output = null;
            var succeeded = false;
            try
            {
                output = Spawn(definition.Entity.Value, Transform(core).Coordinates);
                if (product == MaterialsProduct && !TerminatingOrDeleted(output))
                    _stacks.SetCount((output.Value, null), amount);
                if (!TerminatingOrDeleted(core) && !TerminatingOrDeleted(output) &&
                    (product != MaterialsProduct ||
                     (TryComp<StackComponent>(output, out var stack) && !stack.Unlimited &&
                      stack.StackTypeId == MaterialsStack && stack.Count == amount)))
                {
                    succeeded = true;
                    return true;
                }
            }
            finally
            {
                if (!succeeded)
                {
                    if (output != null && !TerminatingOrDeleted(output))
                        Del(output.Value);
                    if (!TerminatingOrDeleted(core))
                        stockpile.Counts[product] = before;
                }
            }
            return false;
        }
        finally
        {
            _operating.Remove(core);
            UpdateUi(core);
        }
    }
}
