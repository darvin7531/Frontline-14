using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;
using Content.Server.Stack;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Stacks;
using Content.Shared.Vehicle.Components;
using Content.Shared.Vehicle.Systems;
using Content.Shared.War;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarVehicleSnapshot(
    [property: JsonRequired] string VehicleId,
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] float X,
    [property: JsonRequired] float Y,
    [property: JsonRequired] double RotationRadians,
    [property: JsonRequired] Dictionary<string, int> DamageHundredths,
    [property: JsonRequired] List<WarVehicleCargoSnapshot> Cargo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarVehicleCargoSnapshot(
    [property: JsonRequired] string SlotId,
    [property: JsonRequired] WarRefineryStackSnapshot? Stack,
    [property: JsonRequired] WarFactoryCrateSnapshot? Crate);

public sealed partial class WarStrategicSnapshotSystem
{
    [Dependency] private SharedTransformSystem _vehicleTransforms = default!;
    [Dependency] private SharedContainerSystem _vehicleContainers = default!;
    [Dependency] private ItemSlotsSystem _vehicleSlots = default!;
    [Dependency] private StackSystem _vehicleStacks = default!;
    [Dependency] private VehicleSystem _vehicles = default!;

    private readonly HashSet<EntityUid> _restoringVehicles = new();

    private void OnVehicleCargoEject(Entity<VehiclePersistenceComponent> ent, ref ItemSlotEjectAttemptEvent args)
    {
        if (_restoringVehicles.Contains(ent.Owner) || !Usable(ent.Owner) || !Usable(args.Item))
            args.Cancelled = true;
    }

    private void OnVehicleInit(Entity<VehiclePersistenceComponent> ent, ref MapInitEvent args)
    {
        if (string.IsNullOrWhiteSpace(ent.Comp.VehicleId))
            ent.Comp.VehicleId = Guid.NewGuid().ToString();
    }

    // ponytail: the campaign has one ground grid; multiple grids need mapper-owned grid IDs first.
    private EntityUid VehicleGround(MapId mapId)
    {
        var grids = new List<EntityUid>();
        var query = AllEntityQuery<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.MapID == mapId)
            {
                if (!Usable(uid) || !HasComp<MapComponent>(transform.ParentUid))
                    throw new InvalidDataException("Vehicle ground grid is not settled.");
                grids.Add(uid);
            }
        }
        if (grids.Count != 1)
            throw new InvalidDataException("Vehicle persistence requires exactly one ground grid.");
        return grids[0];
    }

    private List<EntityUid> VehicleEntities(MapId mapId)
    {
        var result = new List<EntityUid>();
        var query = AllEntityQuery<VehiclePersistenceComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.MapID != mapId)
                continue;
            if (!Usable(uid))
                throw new InvalidDataException("Persistent vehicle is not settled.");
            result.Add(uid);
        }
        return result;
    }

    private WarVehicleCargoSnapshot ReadVehicleCargo(string id, EntityUid uid)
    {
        if (!Usable(uid) || HasComp<ContainerManagerComponent>(uid) ||
            HasComp<Content.Shared.Storage.StorageComponent>(uid) ||
            HasComp<Content.Shared.Storage.Components.EntityStorageComponent>(uid))
            throw new InvalidDataException("Unsupported or unsettled vehicle cargo.");
        var prototype = MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Cargo has no prototype.");
        if (TryComp<StackComponent>(uid, out var stack) && !HasComp<FrontlineSupplyCrateComponent>(uid))
        {
            var entry = new WarRefineryStackSnapshot(stack.StackTypeId.Id, prototype, stack.Count);
            if (stack.Unlimited)
                throw new InvalidDataException("Unlimited vehicle cargo cannot be persisted.");
            _factories.ValidateStack(entry);
            return new WarVehicleCargoSnapshot(id, entry, null);
        }
        if (TryComp<FrontlineSupplyCrateComponent>(uid, out var crate) && !HasComp<StackComponent>(uid))
        {
            var entry = new WarFactoryCrateSnapshot(prototype, crate.Product.Id, crate.Amount);
            _factories.ValidateCrate(entry);
            return new WarVehicleCargoSnapshot(id, null, entry);
        }
        throw new InvalidDataException("Vehicle cargo must be a material stack or sealed supply crate.");
    }

    private WarVehicleSnapshot ReadVehicle(EntityUid uid, EntityUid ground, bool vacant = false)
    {
        if (!Usable(uid) || !HasComp<VehicleComponent>(uid) || !HasComp<DamageableComponent>(uid) ||
            !TryComp<VehiclePersistenceComponent>(uid, out var identity) ||
            !TryComp<ItemSlotsComponent>(uid, out var slots) ||
            !_vehicles.TryGetOperatorContainer(uid, out var driver) ||
            (vacant && (driver.ContainedEntities.Count != 0 || Comp<VehicleComponent>(uid).Operator != null ||
                HasComp<RelayInputMoverComponent>(uid) ||
                (TryComp<InputMoverComponent>(uid, out var mover) && (mover.HeldMoveButtons != MoveButtons.None ||
                    mover.CurTickWalkMovement != Vector2.Zero || mover.CurTickSprintMovement != Vector2.Zero ||
                    mover.WishDir != Vector2.Zero)) ||
                (TryComp<PhysicsComponent>(uid, out var physics) &&
                    (physics.LinearVelocity != Vector2.Zero || physics.AngularVelocity != 0)))))
            throw new InvalidDataException("Persistent vehicle components or driver seat changed.");
        var transform = Transform(uid);
        if (transform.ParentUid != ground || transform.Anchored)
            throw new InvalidDataException("Persistent vehicle must be directly on the ground grid.");
        var cargo = new List<WarVehicleCargoSnapshot>();
        foreach (var (id, slot) in slots.Slots.OrderBy(pair => pair.Key))
        {
            if (slot.ContainerSlot == null || slot.ContainerSlot.Owner != uid || slot.ContainerSlot.ID != id ||
                !_vehicleContainers.TryGetContainer(uid, id, out var container) || container != slot.ContainerSlot ||
                container == driver)
                throw new InvalidDataException("Vehicle cargo slot identity changed.");
            if (slot.Item is not { } item)
                continue;
            if (!_vehicleContainers.TryGetContainingContainer(item, out var owner) || owner != container)
                throw new InvalidDataException("Vehicle cargo containment changed.");
            cargo.Add(ReadVehicleCargo(id, item));
        }
        foreach (var container in _vehicleContainers.GetAllContainers(uid))
        {
            if (container != driver && !_vehicleSlots.TryGetSlot((uid, slots), container.ID, out _))
                throw new InvalidDataException("Vehicle has an unsupported container.");
        }
        return new WarVehicleSnapshot(identity.VehicleId,
            MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Vehicle has no prototype."),
            transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalRotation.Theta,
            ReadDamage(uid), cargo);
    }

    public List<WarVehicleSnapshot> CaptureVehicles(MapId mapId)
    {
        var vehicles = VehicleEntities(mapId);
        if (vehicles.Any(uid => _restoringVehicles.Contains(uid)))
            throw new InvalidDataException("Vehicle restoration must settle before capture.");
        var result = vehicles.Count == 0 ? new List<WarVehicleSnapshot>() :
            vehicles.Select(uid => ReadVehicle(uid, VehicleGround(mapId))).ToList();
        ValidateVehicles(mapId, result);
        return result;
    }

    private void ValidateVehicles(MapId mapId, List<WarVehicleSnapshot> entries)
    {
        if (entries.Count != 0)
            VehicleGround(mapId);
        var seen = new HashSet<string>();
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.VehicleId) || !seen.Add(entry.VehicleId) ||
                string.IsNullOrWhiteSpace(entry.Prototype) || !float.IsFinite(entry.X) || !float.IsFinite(entry.Y) ||
                !double.IsFinite(entry.RotationRadians) || entry.DamageHundredths == null || entry.Cargo == null ||
                !_prototypes.TryIndex(new EntProtoId(entry.Prototype), out var prototype) || prototype.Abstract ||
                !prototype.HasComp<VehiclePersistenceComponent>(EntityManager.ComponentFactory) ||
                !prototype.HasComp<VehicleComponent>(EntityManager.ComponentFactory) ||
                !prototype.HasComp<DamageableComponent>(EntityManager.ComponentFactory) ||
                !prototype.TryGetComponent<ContainerVehicleComponent>(out var driver, EntityManager.ComponentFactory) ||
                !prototype.TryGetComponent<ItemSlotsComponent>(out var slots, EntityManager.ComponentFactory) ||
                !prototype.TryGetComponent<ContainerManagerComponent>(out var containers, EntityManager.ComponentFactory) ||
                string.IsNullOrWhiteSpace(driver.ContainerId) ||
                !containers.Containers.TryGetValue(driver.ContainerId, out var seat) || seat is not ContainerSlot ||
                slots.Slots.Any(pair => pair.Key == driver.ContainerId || pair.Value.StartingItem != null ||
                    !containers.Containers.TryGetValue(pair.Key, out var container) || container is not ContainerSlot) ||
                containers.Containers.Keys.Any(id => id != driver.ContainerId && !slots.Slots.Any(pair => pair.Key == id)))
                throw new InvalidDataException("Invalid persistent vehicle identity, prototype or state.");
            long total = 0;
            foreach (var (type, amount) in entry.DamageHundredths)
            {
                if (string.IsNullOrWhiteSpace(type) || !_prototypes.HasIndex<DamageTypePrototype>(type) || amount < 0 ||
                    (total += amount) > int.MaxValue)
                    throw new InvalidDataException("Invalid vehicle damage.");
            }
            var cargoSlots = new HashSet<string>();
            foreach (var cargo in entry.Cargo)
            {
                if (cargo == null || string.IsNullOrWhiteSpace(cargo.SlotId) || !cargoSlots.Add(cargo.SlotId) ||
                    cargo.SlotId == driver.ContainerId || !slots.Slots.Any(pair => pair.Key == cargo.SlotId) ||
                    (cargo.Stack == null) == (cargo.Crate == null))
                    throw new InvalidDataException("Invalid vehicle cargo slot or claim.");
                if (cargo.Stack is { } stack)
                {
                    _factories.ValidateStack(stack);
                    var stackPrototype = _prototypes.Index(new EntProtoId(stack.Prototype));
                    if (stackPrototype.HasComp<ContainerManagerComponent>(EntityManager.ComponentFactory) ||
                        stackPrototype.HasComp<Content.Shared.Storage.StorageComponent>(EntityManager.ComponentFactory) ||
                        stackPrototype.HasComp<Content.Shared.Storage.Components.EntityStorageComponent>(EntityManager.ComponentFactory) ||
                        stackPrototype.HasComp<FrontlineSupplyCrateComponent>(EntityManager.ComponentFactory))
                        throw new InvalidDataException("Unsupported nested vehicle cargo.");
                }
                else
                {
                    _factories.ValidateCrate(cargo.Crate!);
                    if (_prototypes.Index(new EntProtoId(cargo.Crate!.Prototype)).HasComp<StackComponent>(EntityManager.ComponentFactory))
                        throw new InvalidDataException("Ambiguous vehicle cargo claim.");
                }
            }
        }
    }

    private static bool SameVehicle(WarVehicleSnapshot actual, WarVehicleSnapshot expected) =>
        actual.VehicleId == expected.VehicleId && actual.Prototype == expected.Prototype &&
        actual.X == expected.X && actual.Y == expected.Y && actual.RotationRadians == expected.RotationRadians &&
        actual.DamageHundredths.OrderBy(pair => pair.Key).SequenceEqual(expected.DamageHundredths.OrderBy(pair => pair.Key)) &&
        actual.Cargo.OrderBy(entry => entry.SlotId).SequenceEqual(expected.Cargo.OrderBy(entry => entry.SlotId));

    private void ValidateStagedVehicles(MapId mapId, List<WarVehicleSnapshot> entries,
        List<EntityUid> fresh, List<EntityUid> staged, List<EntityUid> claims, bool partial = false, bool skipLast = false)
    {
        // ponytail: whole-set readback per callback is quadratic; cap vehicle claims if campaign scale grows.
        if ((!partial && staged.Count != entries.Count) || staged.Count > entries.Count ||
            staged.Distinct().Count() != staged.Count || claims.Distinct().Count() != claims.Count ||
            !VehicleEntities(mapId).ToHashSet().SetEquals(fresh.Concat(staged)))
            throw new InvalidDataException("Persistent vehicle set changed during restoration.");
        if (staged.Count == 0)
            return;
        var ground = VehicleGround(mapId);
        var index = 0;
        for (var i = 0; i < staged.Count - (skipLast ? 1 : 0); i++)
        {
            var entry = entries[i];
            var cargo = entry.Cargo.Take(Math.Max(0, claims.Count - index)).ToList();
            if (!SameVehicle(ReadVehicle(staged[i], ground, vacant: true), entry with { Cargo = cargo }))
                throw new InvalidDataException("Restored vehicle changed during native callbacks.");
            foreach (var expected in cargo)
            {
                if (Comp<ItemSlotsComponent>(staged[i]).Slots[expected.SlotId].Item != claims[index++])
                    throw new InvalidDataException("Restored vehicle cargo entity changed.");
            }
        }
        if (!skipLast && (index != claims.Count || (!partial && index != entries.Sum(entry => entry.Cargo.Count))))
            throw new InvalidDataException("Restored vehicle cargo count changed.");
    }

    private void StageVehicles(MapId mapId, List<WarVehicleSnapshot> entries, List<EntityUid> fresh,
        List<EntityUid> staged, List<EntityUid> claims, Action validateOtherSlices)
    {
        foreach (var entry in entries)
        {
            var uid = Spawn(new EntProtoId(entry.Prototype), new EntityCoordinates(VehicleGround(mapId), new Vector2(entry.X, entry.Y)));
            staged.Add(uid);
            _restoringVehicles.Add(uid);
            validateOtherSlices();
            ValidateStagedVehicles(mapId, entries, fresh, staged, claims, partial: true, skipLast: true);
            Comp<VehiclePersistenceComponent>(uid).VehicleId = entry.VehicleId;
            _vehicleTransforms.SetLocalRotation(uid, new Angle(entry.RotationRadians));
            validateOtherSlices();
            ValidateStagedVehicles(mapId, entries, fresh, staged, claims, partial: true, skipLast: true);
            _damage.SetDamage(uid, new DamageSpecifier
            {
                DamageDict = entry.DamageHundredths.ToDictionary(pair => new ProtoId<DamageTypePrototype>(pair.Key),
                    pair => FixedPoint2.FromHundredths(pair.Value)),
            });
            Check();
            foreach (var cargo in entry.Cargo)
            {
                var item = Spawn(new EntProtoId(cargo.Stack?.Prototype ?? cargo.Crate!.Prototype));
                claims.Add(item); // Track before eventful count restoration and insertion.
                validateOtherSlices();
                ValidateStagedVehicles(mapId, entries, fresh, staged, claims.Take(claims.Count - 1).ToList(), partial: true);
                if (cargo.Stack is { } stack)
                    _vehicleStacks.SetCount((item, null), stack.Count);
                else
                    _factories.RestoreCrate(item, cargo.Crate!);
                validateOtherSlices();
                ValidateStagedVehicles(mapId, entries, fresh, staged, claims.Take(claims.Count - 1).ToList(), partial: true);
                if (ReadVehicleCargo(cargo.SlotId, item) != cargo || !_vehicleSlots.TryInsert(uid, cargo.SlotId, item, null))
                    throw new InvalidDataException("Vehicle did not retain its exact cargo.");
                Check();
            }
        }
        ValidateStagedVehicles(mapId, entries, fresh, staged, claims);

        void Check()
        {
            validateOtherSlices();
            ValidateStagedVehicles(mapId, entries, fresh, staged, claims, partial: true);
        }
    }

    private void CommitVehicles(MapId mapId, List<WarVehicleSnapshot> entries, List<EntityUid> fresh,
        List<EntityUid> staged, List<EntityUid> claims, Action validateOtherSlices, ref bool commitStarted)
    {
        ValidateStagedVehicles(mapId, entries, fresh, staged, claims);
        while (fresh.Count != 0)
        {
            var uid = fresh[^1];
            commitStarted = true;
            Del(uid); // The saved set is authoritative, including empty/destroyed vehicles.
            fresh.RemoveAt(fresh.Count - 1);
            validateOtherSlices();
            ValidateStagedVehicles(mapId, entries, fresh, staged, claims);
        }
        ValidateStagedVehicles(mapId, entries, fresh, staged, claims);
    }

    private void QueueStrategicClaims(IEnumerable<EntityUid> claims)
    {
        Exception? failure = null;
        foreach (var uid in claims.Distinct())
        {
            if (TerminatingOrDeleted(uid))
                continue;
            try { QueueDel(uid); }
            catch (Exception e) { failure ??= e; }
        }
        if (failure != null)
            throw failure;
    }

    private void DeleteVehicleClaims(List<EntityUid> vehicles, List<EntityUid> cargo)
    {
        QueueStrategicClaims(cargo.Concat(vehicles));
        foreach (var uid in cargo.Concat(vehicles))
        {
            if (!TerminatingOrDeleted(uid))
                Del(uid);
        }
    }
}
