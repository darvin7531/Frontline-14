using System.IO;
using System.Linq;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.War;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class FrontlineStockpileSystem
{
    [Dependency] private SharedStorageSystem _storage = default!;

    private void InitializeStorage()
    {
        SubscribeLocalEvent<FrontlineStockpileComponent, StorageInteractAttemptEvent>(OnStorageAccess);
        SubscribeLocalEvent<FrontlineStockpileComponent, BoundUIClosedEvent>(OnStockpileClosed);
        SubscribeLocalEvent<FrontlineStockpileComponent, StoragePlayerTransferredEvent>(OnStorageTransferred);
    }

    private bool TryStoredProduct(EntityUid item, out ProtoId<FrontlineSupplyProductPrototype> product, out int amount)
    {
        if (TryInput(item, out product, out amount)) return true;
        product = default;
        amount = 0;
        var prototype = MetaData(item).EntityPrototype?.ID;
        foreach (var definition in _prototypes.EnumeratePrototypes<FrontlineSupplyProductPrototype>())
        {
            if (definition.Entity?.Id != prototype || !TryProduct(definition.ID, out _)) continue;
            product = definition.ID;
            amount = TryComp<StackComponent>(item, out var stack) && !stack.Unlimited ? stack.Count : 1;
            return true;
        }
        return false;
    }

    public Dictionary<string, int> ReadCounts(EntityUid core)
    {
        if (_operating.Contains(core) || _production.NativeTransferActive(core))
            throw new InvalidDataException("Stockpile operation must settle before capture.");
        return SumCounts(core);
    }

    private Dictionary<string, int> SumCounts(EntityUid core)
    {
        var result = Comp<FrontlineStockpileComponent>(core).Counts.ToDictionary(pair => pair.Key.Id, pair => pair.Value);
        if (!TryComp<StorageComponent>(core, out var storage)) return result;
        foreach (var item in storage.Container.ContainedEntities)
        {
            if (TerminatingOrDeleted(item) || EntityManager.IsQueuedForDeletion(item) ||
                !storage.StoredItems.ContainsKey(item) || !TryStoredProduct(item, out var product, out var amount))
                throw new InvalidDataException("Unsettled stockpile physical claim.");
            result[product.Id] = checked(result.GetValueOrDefault(product.Id) + amount);
        }
        return result;
    }

    // ponytail: native grid is a bounded staging page; refill from remaining counts after a withdrawal.
    public void Materialize(EntityUid core)
    {
        if (TerminatingOrDeleted(core) || EntityManager.IsQueuedForDeletion(core) ||
            !TryComp<StorageComponent>(core, out var storage) || !_operating.Add(core)) return;
        try
        {
            var counts = Comp<FrontlineStockpileComponent>(core).Counts;
            foreach (var product in counts.Keys.OrderBy(id => id.Id).ToArray())
            {
                if (!TryProduct(product, out var definition) || definition.Entity == null) continue;
                while (counts.GetValueOrDefault(product) > 0)
                {
                    var before = counts[product];
                    var amount = product == MaterialsProduct ? Math.Min(before, 30) : 1;
                    EntityUid? item = null;
                    var committed = false;
                    counts[product] = before - amount;
                    try
                    {
                        item = Spawn(definition.Entity.Value, Transform(core).Coordinates);
                        if (product == MaterialsProduct && !TerminatingOrDeleted(item))
                            _stacks.SetCount((item.Value, null), amount);
                        if (TerminatingOrDeleted(core) || EntityManager.IsQueuedForDeletion(core) ||
                            TerminatingOrDeleted(item) || EntityManager.IsQueuedForDeletion(item.Value) ||
                            !TryStoredProduct(item.Value, out var actual, out var quantity) || actual != product || quantity != amount ||
                            !_storage.Insert(core, item.Value, out _, stackAutomatically: false) ||
                            !storage.Container.Contains(item.Value) ||
                            TerminatingOrDeleted(item) || EntityManager.IsQueuedForDeletion(item.Value)) return;
                        committed = true;
                    }
                    finally
                    {
                        if (!committed)
                        {
                            if (item is { } staged && !TerminatingOrDeleted(staged)) Del(staged);
                            if (!TerminatingOrDeleted(core)) counts[product] = before;
                        }
                    }
                }
            }
        }
        finally { _operating.Remove(core); }
    }

    private void OnStockpileClosed(Entity<FrontlineStockpileComponent> ent, ref BoundUIClosedEvent args)
    {
        if (args.UiKey is FrontlineStockpileUiKey)
            _ui.CloseUi(ent.Owner, StorageComponent.StorageUiKey.Key, args.Actor);
    }

    private void OnStorageAccess(Entity<FrontlineStockpileComponent> ent, ref StorageInteractAttemptEvent args)
    {
        if (args.User is not { } actor || _operating.Contains(ent.Owner) || !CanAccess(ent, actor) ||
            EntityManager.IsQueuedForDeletion(ent.Owner) ||
            args.Inserting && (args.Item is not { } item || !TryInput(item, out var product, out var amount) ||
                amount > int.MaxValue - SumCounts(ent).GetValueOrDefault(product.Id)))
            args.Cancelled = true;
    }

    private void OnStorageTransferred(Entity<FrontlineStockpileComponent> ent, ref StoragePlayerTransferredEvent args)
    {
        if (args.Target == ent.Owner && Comp<StorageComponent>(ent).Container.Contains(args.Item) &&
            TryInput(args.Item, out var product, out var amount) && _operating.Add(ent.Owner))
        {
            var before = ent.Comp.Counts.GetValueOrDefault(product);
            try
            {
                ent.Comp.Counts[product] = checked(before + amount);
                Del(args.Item);
            }
            catch
            {
                if (!TerminatingOrDeleted(args.Item)) ent.Comp.Counts[product] = before;
                throw;
            }
            finally { _operating.Remove(ent.Owner); }
        }
        Materialize(ent);
        UpdateUi(ent);
    }
}
