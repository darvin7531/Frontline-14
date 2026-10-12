using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.War;
using Robust.Shared.Containers;

namespace Content.Server.War;

public sealed partial class FrontlineProductionSystem
{
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private FrontlineFactorySystem _factories = default!;
    [Dependency] private FrontlineRefinerySystem _refineries = default!;

    private readonly Dictionary<EntityUid, (EntityUid Actor, EntityUid Item)> _nativeTransfers = new();
    private readonly Dictionary<(EntityUid Storage, EntityUid Actor), NetEntity[]> _storageViews = new();
    private DateTimeOffset _nextStorageViewRefresh;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = DateTimeOffset.UtcNow;
        if (Flushing || now < _nextStorageViewRefresh) return;
        _nextStorageViewRefresh = now.AddSeconds(1);
        var ui = EntityManager.System<SharedUserInterfaceSystem>();
        var query = EntityQueryEnumerator<FrontlineProductionStorageComponent, UserInterfaceComponent>();
        while (query.MoveNext(out var uid, out var link, out var interfaces))
            if (link.Output)
                foreach (var actor in ui.GetActors((uid, interfaces), StorageComponent.StorageUiKey.Key))
                    UpdateStorageView(link.Machine, actor);
    }

    public bool NativeTransferActive(EntityUid machine) => _nativeTransfers.ContainsKey(machine);
    public bool NativeTransferItem(EntityUid machine, EntityUid item) =>
        _nativeTransfers.TryGetValue(machine, out var transfer) && transfer.Item == item;

    public bool OwnNativeTransfer(EntityUid machine, EntityUid actor, EntityUid? item) =>
        _nativeTransfers.TryGetValue(machine, out var transfer) && transfer.Actor == actor &&
        (item == null || transfer.Item == item);

    private void OnTransferAttempt(Entity<StorageComponent> ent, ref StoragePlayerTransferAttemptEvent args)
    {
        var owner = TryComp<FrontlineProductionStorageComponent>(ent, out var link) ? link.Machine : ent.Owner;
        if (link == null && !HasComp<FrontlineStockpileComponent>(owner) && !HasComp<FrontlineVehicleCargoComponent>(owner)) return;
        if (args.End)
        {
            if (OwnNativeTransfer(owner, args.User, args.Item))
            {
                _nativeTransfers.Remove(owner);
                if (HasComp<FrontlineFactoryComponent>(owner)) _factories.NativeTransferFinished(owner);
                else if (HasComp<FrontlineRefineryComponent>(owner)) _refineries.NativeTransferFinished(owner);
            }
            return;
        }
        if (!_nativeTransfers.TryAdd(owner, (args.User, args.Item))) args.Cancelled = true;
    }

    private void InitializeStorage()
    {
        SubscribeLocalEvent<StorageComponent, StoragePlayerTransferAttemptEvent>(OnTransferAttempt);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, BoundUIOpenedEvent>(OnStorageOpened);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, BoundUIClosedEvent>(OnStorageViewClosed);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, FrontlineProductionStorageViewRequestMessage>(OnStorageViewRequest);
        SubscribeLocalEvent<FrontlineFactoryComponent, BoundUIOpenedEvent>(OnFactoryStorageOpened);
        SubscribeLocalEvent<FrontlineRefineryComponent, BoundUIOpenedEvent>(OnRefineryStorageOpened);
        SubscribeLocalEvent<FrontlineFactoryComponent, BoundUIClosedEvent>(OnFactoryStorageClosed);
        SubscribeLocalEvent<FrontlineRefineryComponent, BoundUIClosedEvent>(OnRefineryStorageClosed);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, StorageInteractAttemptEvent>(OnStorageAccess);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, ContainerIsInsertingAttemptEvent>(OnEconomicInsert);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, ContainerIsRemovingAttemptEvent>(OnEconomicRemove);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, StoragePlayerTransferredEvent>(OnStorageTransferred);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, EntInsertedIntoContainerMessage>(OnStorageInserted);
        SubscribeLocalEvent<FrontlineProductionStorageComponent, EntRemovedFromContainerMessage>(OnStorageRemoved);
    }

    private void OnFactoryStorageOpened(Entity<FrontlineFactoryComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (args.UiKey is FrontlineFactoryUiKey) OpenStorage(ent, args.Actor);
    }

    private void OnRefineryStorageOpened(Entity<FrontlineRefineryComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (args.UiKey is FrontlineRefineryUiKey) OpenStorage(ent, args.Actor);
    }

    private void OnFactoryStorageClosed(Entity<FrontlineFactoryComponent> ent, ref BoundUIClosedEvent args)
    {
        if (args.UiKey is FrontlineFactoryUiKey) CloseStorage(ent, args.Actor);
    }

    private void OnRefineryStorageClosed(Entity<FrontlineRefineryComponent> ent, ref BoundUIClosedEvent args)
    {
        if (args.UiKey is FrontlineRefineryUiKey) CloseStorage(ent, args.Actor);
    }

    private void CloseStorage(EntityUid machine, EntityUid actor)
    {
        var ui = EntityManager.System<SharedUserInterfaceSystem>();
        foreach (var id in new[] { FrontlineFactoryComponent.InputContainerId, FrontlineFactoryComponent.OutputContainerId })
            if (TryGetContainer(machine, id, out var container)) ui.CloseUi(container.Owner, StorageComponent.StorageUiKey.Key, actor);
    }

    private void OnStorageOpened(Entity<FrontlineProductionStorageComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (ent.Comp.Output) UpdateStorageView(ent.Comp.Machine, args.Actor);
    }

    private void OnStorageViewRequest(Entity<FrontlineProductionStorageComponent> ent, ref FrontlineProductionStorageViewRequestMessage args)
    {
        if (!ent.Comp.Output) return;
        _storageViews.Remove((ent.Owner, args.Actor));
        UpdateStorageView(ent.Comp.Machine, args.Actor);
    }

    private void OnStorageViewClosed(Entity<FrontlineProductionStorageComponent> ent, ref BoundUIClosedEvent args) =>
        _storageViews.Remove((ent.Owner, args.Actor));

    public void UpdateStorageView(EntityUid machine, EntityUid actor)
    {
        var ui = EntityManager.System<Robust.Server.GameObjects.UserInterfaceSystem>();
        if (NativeTransferActive(machine) || !TryGetContainer(machine, FrontlineFactoryComponent.OutputContainerId, out var output) ||
            !ui.IsUiOpen(output.Owner, StorageComponent.StorageUiKey.Key, actor)) return;
        var items = output.ContainedEntities.Where(item => HasComp<FrontlineFactoryComponent>(machine)
            ? _factories.CanAccessNativeStorage(machine, actor, item, true, false)
            : _refineries.CanAccessNativeStorage(machine, actor, item, true, false)).Select(item => GetNetEntity(item)).ToArray();
        if (_storageViews.TryGetValue((output.Owner, actor), out var previous) && previous.SequenceEqual(items)) return;
        _storageViews[(output.Owner, actor)] = items;
        ui.ServerSendUiMessage(output.Owner,
            StorageComponent.StorageUiKey.Key, new FrontlineProductionStorageViewMessage(items), actor);
    }

    public Container CreateStorage(EntityUid machine, string id, bool output)
    {
        var parent = _containers.EnsureContainer<Container>(machine, id);
        foreach (var entity in parent.ContainedEntities)
        {
            if (TryComp<FrontlineProductionStorageComponent>(entity, out var existing) &&
                existing.Machine == machine && existing.Output == output &&
                TryComp<StorageComponent>(entity, out var storage))
                return storage.Container;
        }
        var retained = parent.ContainedEntities.ToArray();
        var child = Spawn("FrontlineProductionStorage", Transform(machine).Coordinates);
        var link = AddComp<FrontlineProductionStorageComponent>(child);
        link.Machine = machine;
        link.Output = output;
        Dirty(child, link);
        if (!_containers.Insert(child, parent))
            throw new InvalidOperationException("Machine did not retain its native storage child.");
        var container = Comp<StorageComponent>(child).Container;
        foreach (var item in retained)
        {
            if (!_containers.Insert(item, container) || !container.Contains(item))
                throw new InvalidOperationException("Machine storage migration did not retain the original item.");
        }
        return container;
    }

    public bool TryGetContainer(EntityUid machine, string id, [NotNullWhen(true)] out BaseContainer? container)
    {
        container = null;
        var output = id == FrontlineFactoryComponent.OutputContainerId;
        Container? expected = null;
        if (TryComp<FrontlineFactoryComponent>(machine, out var factory))
            expected = output ? factory.OutputContainer : factory.InputContainer;
        else if (TryComp<FrontlineRefineryComponent>(machine, out var refinery))
            expected = output ? refinery.OutputContainer : refinery.InputContainer;
        if (expected == null || !_containers.TryGetContainer(machine, id, out var parent) ||
            !parent.Contains(expected.Owner) ||
            !TryComp<FrontlineProductionStorageComponent>(expected.Owner, out var link) ||
            link.Machine != machine || link.Output != output ||
            !TryComp<StorageComponent>(expected.Owner, out var storage) || storage.Container != expected)
            return false;
        container = expected;
        return true;
    }

    public void OpenStorage(EntityUid machine, EntityUid actor)
    {
        if (TryGetContainer(machine, FrontlineFactoryComponent.InputContainerId, out var input))
            _storage.OpenStorageUI(input.Owner, actor);
        if (TryGetContainer(machine, FrontlineFactoryComponent.OutputContainerId, out var output))
            _storage.OpenStorageUI(output.Owner, actor);
    }

    private void OnEconomicInsert(Entity<FrontlineProductionStorageComponent> ent, ref ContainerIsInsertingAttemptEvent args)
    {
        if (NativeTransferActive(ent.Comp.Machine) && !NativeTransferItem(ent.Comp.Machine, args.EntityUid)) args.Cancel();
    }

    private void OnEconomicRemove(Entity<FrontlineProductionStorageComponent> ent, ref ContainerIsRemovingAttemptEvent args)
    {
        if (NativeTransferActive(ent.Comp.Machine) && !NativeTransferItem(ent.Comp.Machine, args.EntityUid)) args.Cancel();
    }

    private void OnStorageTransferred(Entity<FrontlineProductionStorageComponent> child, ref StoragePlayerTransferredEvent args)
    {
        if (child.Comp.Output)
            RemComp<FrontlineProductionClaimComponent>(args.Item);
    }

    private void OnStorageAccess(Entity<FrontlineProductionStorageComponent> child, ref StorageInteractAttemptEvent args)
    {
        if (args.User is not { } actor ||
            !(HasComp<FrontlineFactoryComponent>(child.Comp.Machine)
                ? _factories.CanAccessNativeStorage(child.Comp.Machine, actor, args.Item, child.Comp.Output, args.Inserting)
                : _refineries.CanAccessNativeStorage(child.Comp.Machine, actor, args.Item, child.Comp.Output, args.Inserting)))
            args.Cancelled = true;
    }

    private void OnStorageInserted(Entity<FrontlineProductionStorageComponent> child, ref EntInsertedIntoContainerMessage args)
    {
        if (!TerminatingOrDeleted(child.Comp.Machine))
            RaiseLocalEvent(child.Comp.Machine, args);
    }

    private void OnStorageRemoved(Entity<FrontlineProductionStorageComponent> child, ref EntRemovedFromContainerMessage args)
    {
        if (!TerminatingOrDeleted(child.Comp.Machine))
            RaiseLocalEvent(child.Comp.Machine, args);
    }
}
