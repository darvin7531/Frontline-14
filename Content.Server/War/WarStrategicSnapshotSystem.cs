using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Content.Server.GameTicking;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
using Content.Shared.Vehicle.Components;
using Content.Shared.War;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.War;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarStrategicSnapshot(
    [property: JsonRequired] int SnapshotVersion,
    [property: JsonRequired] int WarId,
    [property: JsonRequired] List<WarBaseSnapshot> Bases)
{
    [JsonRequired]
    public List<WarResourceFieldSnapshot> Resources { get; init; } = new();

    [JsonRequired]
    public List<WarRefinerySnapshot> Refineries { get; init; } = new();

    [JsonRequired]
    public List<WarFactorySnapshot> Factories { get; init; } = new();

    [JsonRequired]
    public List<WarVehicleSnapshot> Vehicles { get; init; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarFactorySnapshot(
    [property: JsonRequired] string FactoryId,
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] List<WarFactoryJobSnapshot> Jobs,
    [property: JsonRequired] List<WarRefineryStackSnapshot> Inputs,
    [property: JsonRequired] List<WarFactoryCrateSnapshot> Outputs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarFactoryJobSnapshot(
    [property: JsonRequired] string Recipe,
    [property: JsonRequired] long RemainingTicks,
    [property: JsonRequired] WarProductionJobClaim? Claim = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarFactoryCrateSnapshot(
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] string Product,
    [property: JsonRequired] int Amount,
    [property: JsonRequired] WarProductionOutputClaim? Claim = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarRefinerySnapshot(
    [property: JsonRequired] string RefineryId,
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] List<WarRefineryJobSnapshot> Jobs,
    [property: JsonRequired] List<WarRefineryStackSnapshot> Inputs,
    [property: JsonRequired] List<WarRefineryStackSnapshot> Outputs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarRefineryJobSnapshot(
    [property: JsonRequired] string Recipe,
    [property: JsonRequired] long RemainingTicks,
    [property: JsonRequired] long Batches = 1,
    [property: JsonRequired] WarProductionJobClaim? Claim = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarRefineryStackSnapshot(
    [property: JsonRequired] string StackId,
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] int Count,
    [property: JsonRequired] WarProductionOutputClaim? Claim = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarResourceFieldSnapshot(
    [property: JsonRequired] string FieldId,
    [property: JsonRequired] int RemainingReserveNodes,
    [property: JsonRequired] bool FieldInitialized,
    [property: JsonRequired] FrontlineResourceFieldState State,
    [property: JsonRequired] long ReplacementRemainingTicks,
    [property: JsonRequired] long ReplenishmentRemainingTicks,
    [property: JsonRequired] List<WarResourceNodeSnapshot> Nodes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarResourceNodeSnapshot(
    [property: JsonRequired] string SlotId,
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] int RemainingYield);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarBaseSnapshot(
    [property: JsonRequired] string TerritoryId,
    [property: JsonRequired] string ObjectiveKind,
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] string? FactionId,
    [property: JsonRequired] Dictionary<string, int> Counts)
{
    // Constructor defaults support fresh synthetic snapshots, not missing fields in persisted JSON.
    [JsonRequired]
    public Dictionary<string, int> DamageHundredths { get; init; } = new();

    [JsonRequired]
    public int RuinMaterialDeposited { get; init; }
}

/// <summary>Technical-restart persistence for strategic objectives, resources, machines and opted-in vehicles.</summary>
public sealed partial class WarStrategicSnapshotSystem : EntitySystem
{
    // Earlier versions omitted vehicle claims; never accept omitted state as fresh defaults.
    public const int SnapshotVersion = 8;
    public static readonly ResPath SavePath = new("/persistent-war-strategic.json");
    public static readonly ResPath TemporaryPath = new("/persistent-war-strategic.json.tmp");
    public static readonly ResPath BackupPath = new("/persistent-war-strategic.json.bak");

    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private TownHallSystem _halls = default!;
    [Dependency] private FrontlineStockpileSystem _stockpiles = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private FrontlineResourceFieldSystem _resourceFields = default!;
    [Dependency] private FrontlineRefinerySystem _refineries = default!;
    [Dependency] private FrontlineFactorySystem _factories = default!;

    private EntityUid? _loadedMap;
    private int _loadedWarId;
    private readonly HashSet<EntityUid> _failedMaps = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnCleanup);
        SubscribeLocalEvent<VehiclePersistenceComponent, MapInitEvent>(OnVehicleInit);
        SubscribeLocalEvent<VehiclePersistenceComponent, ItemSlotEjectAttemptEvent>(OnVehicleCargoEject);
        EntityManager.BeforeEntityFlush += OnBeforeEntityFlush;
    }

    public override void Shutdown()
    {
        EntityManager.BeforeEntityFlush -= OnBeforeEntityFlush;
        base.Shutdown();
    }

    private void OnBeforeEntityFlush()
    {
        // Orderly server cleanup does not raise RoundRestartCleanupEvent.
        if (EntityManager.ShuttingDown)
            SaveLoadedMap();
    }

    private bool Usable(EntityUid uid) =>
        !TerminatingOrDeleted(uid) && !EntityManager.IsQueuedForDeletion(uid);

    private Dictionary<string, EntityUid> GetObjectives(MapId mapId)
    {
        var result = new Dictionary<string, EntityUid>();
        void Add(EntityUid uid, string id, TransformComponent xform)
        {
            if (xform.MapID != mapId || !Usable(uid))
                return;
            if (string.IsNullOrWhiteSpace(id) || id == "Unassigned" ||
                !_territories.Contains(new TerritoryId(id), xform.Coordinates, includePaused: true) || !result.TryAdd(id, uid))
                throw new InvalidDataException("Invalid or duplicate strategic objective.");
        }
        var halls = AllEntityQuery<TownHallComponent, TransformComponent>();
        while (halls.MoveNext(out var uid, out var hall, out var xform))
            Add(uid, hall.TerritoryId, xform);
        var ruins = AllEntityQuery<TownHallRuinComponent, TransformComponent>();
        while (ruins.MoveNext(out var uid, out var ruin, out var xform))
            Add(uid, ruin.TerritoryId, xform);
        if (!result.Keys.ToHashSet().SetEquals(_territories.GetTerritories(mapId, includePaused: true).Select(id => id.Id)))
            throw new InvalidDataException("Strategic objectives do not cover the loaded territories.");
        return result;
    }

    private static void ValidateHeader(WarStrategicSnapshot snapshot)
    {
        if (snapshot.SnapshotVersion != SnapshotVersion || snapshot.WarId <= 0 ||
            snapshot.Bases == null || snapshot.Resources == null || snapshot.Refineries == null || snapshot.Factories == null ||
            snapshot.Vehicles == null)
            throw new InvalidDataException("Invalid strategic snapshot header.");
    }

    private void Validate(WarStrategicSnapshot snapshot, Dictionary<string, EntityUid> objectives)
    {
        ValidateHeader(snapshot);
        if (snapshot.Bases.Count != objectives.Count)
            throw new InvalidDataException("Invalid strategic objective count.");
        var seen = new HashSet<string>();
        foreach (var entry in snapshot.Bases)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.TerritoryId) ||
                !objectives.ContainsKey(entry.TerritoryId) || !seen.Add(entry.TerritoryId) ||
                string.IsNullOrWhiteSpace(entry.Prototype) ||
                !_prototypes.TryIndex(new EntProtoId(entry.Prototype), out var prototype) || prototype.Abstract ||
                entry.Counts == null || !_stockpiles.ValidCounts(entry.Counts))
                throw new InvalidDataException("Invalid strategic base, prototype or stockpile.");
            var hall = prototype.TryGetComponent<TownHallComponent>(out var hallComponent, EntityManager.ComponentFactory);
            var ruin = prototype.TryGetComponent<TownHallRuinComponent>(out var ruinComponent, EntityManager.ComponentFactory);
            if (!prototype.HasComp<DamageableComponent>(EntityManager.ComponentFactory) || entry.DamageHundredths == null)
                throw new InvalidDataException("Invalid strategic damage component or amounts.");
            long totalDamage = 0;
            foreach (var (type, amount) in entry.DamageHundredths)
            {
                if (string.IsNullOrWhiteSpace(type) || !_prototypes.HasIndex<DamageTypePrototype>(type) || amount < 0)
                    throw new InvalidDataException("Invalid strategic damage type or amount.");
                totalDamage += amount;
                if (totalDamage > int.MaxValue)
                    throw new InvalidDataException("Strategic total damage overflows FixedPoint2.");
            }
            if (entry.ObjectiveKind == "hall")
            {
                if (!hall || ruin || entry.RuinMaterialDeposited != 0 || string.IsNullOrWhiteSpace(entry.FactionId) ||
                    !_prototypes.HasIndex<FrontlineFactionPrototype>(entry.FactionId) ||
                    hallComponent!.FactionId != entry.FactionId ||
                    !prototype.HasComp<FrontlineStockpileComponent>(EntityManager.ComponentFactory))
                    throw new InvalidDataException("Invalid strategic hall faction or components.");
            }
            else if (entry.ObjectiveKind != "ruin" || !ruin || hall || entry.FactionId != null || entry.Counts.Count != 0 ||
                     ruinComponent!.RequiredBasicMaterials <= 0 || entry.RuinMaterialDeposited < 0 ||
                     entry.RuinMaterialDeposited >= ruinComponent.RequiredBasicMaterials)
                throw new InvalidDataException("Invalid strategic ruin.");
        }
    }

    // JsonSerializer otherwise silently accepts duplicate object keys (including product counts).
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>();
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                    throw new InvalidDataException("Duplicate strategic JSON key.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                RejectDuplicateKeys(child);
        }
    }

    internal static WarStrategicSnapshot ReadSnapshot(JsonElement document)
    {
        RejectDuplicateKeys(document);
        if (document.TryGetProperty("SnapshotVersion", out var version) && version.GetInt32() is 6 or 7)
        {
            // v6/v7 jobs were anonymous public claims; only v6 omitted its one-batch marker.
            var legacyVersion = version.GetInt32();
            var upgraded = JsonNode.Parse(document.GetRawText())!.AsObject();
            foreach (var refinery in upgraded["Refineries"]!.AsArray())
            foreach (var job in refinery!["Jobs"]!.AsArray())
            {
                if (legacyVersion == 6)
                {
                    if (job!.AsObject().ContainsKey("Batches"))
                        throw new InvalidDataException("v6 refinery jobs cannot contain batch claims.");
                    job["Batches"] = 1;
                }
                else if (!job!.AsObject().ContainsKey("Batches"))
                    throw new InvalidDataException("v7 refinery jobs require batch claims.");
            }
            foreach (var kind in new[] { "Factories", "Refineries" })
            foreach (var machine in upgraded[kind]!.AsArray())
            {
                foreach (var job in machine!["Jobs"]!.AsArray())
                {
                    if (job!.AsObject().ContainsKey("Claim"))
                        throw new InvalidDataException("Legacy jobs cannot contain ownership or payment claims.");
                    job["Claim"] = JsonSerializer.SerializeToNode(new WarProductionJobClaim(Guid.NewGuid(), null, true, []));
                }
                foreach (var goods in machine!["Inputs"]!.AsArray().Concat(machine["Outputs"]!.AsArray()))
                {
                    if (goods!.AsObject().ContainsKey("Claim"))
                        throw new InvalidDataException("Legacy physical goods cannot contain ownership claims.");
                    goods["Claim"] = null;
                }
            }
            foreach (var vehicle in upgraded["Vehicles"]!.AsArray())
            foreach (var cargo in vehicle!["Cargo"]!.AsArray())
            {
                foreach (var type in new[] { "Stack", "Crate" })
                {
                    if (cargo![type] is not JsonObject goods) continue;
                    if (goods.ContainsKey("Claim")) throw new InvalidDataException("Legacy cargo cannot contain claims.");
                    goods["Claim"] = null;
                }
            }
            upgraded["SnapshotVersion"] = SnapshotVersion;
            return upgraded.Deserialize<WarStrategicSnapshot>() ??
                throw new InvalidDataException("Empty strategic snapshot.");
        }
        return document.Deserialize<WarStrategicSnapshot>() ??
            throw new InvalidDataException("Empty strategic snapshot.");
    }

    /// <summary>Call once on the validated, initialized fresh map, before any player deployment.</summary>
    public void Restore(EntityUid map, int warId)
    {
        if (!Usable(map) || _failedMaps.Contains(map))
            throw new InvalidDataException("Strategic restore requires a live map.");
        if (_loadedMap == map)
            return;
        var mapId = Comp<MapComponent>(map).MapId;
        var staged = new List<EntityUid>();
        var stagedNodes = new List<EntityUid>();
        var stagedStacks = new List<EntityUid>();
        var restoringRefineries = new List<EntityUid>();
        var stagedFactoryClaims = new List<EntityUid>();
        var restoringFactories = new List<EntityUid>();
        var stagedVehicles = new List<EntityUid>();
        var stagedVehicleCargo = new List<EntityUid>();
        var accepted = false;
        var commitStarted = false;
        try
        {
            var data = _resources.UserData;
            // A missing primary after rotation means the backup is the last committed snapshot.
            var path = data.Exists(SavePath) ? SavePath : BackupPath;
            if (!data.Exists(path))
            {
                accepted = true;
                return;
            }
            using var stream = data.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(stream);
            }
            catch (JsonException) when (path == SavePath && data.Exists(BackupPath))
            {
                // Recover syntax corruption only; schema and paid-claim validation still refuse invalid data.
                using var backup = data.Open(BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                parsed = JsonDocument.Parse(backup);
            }
            using var document = parsed;
            RejectDuplicateKeys(document.RootElement);
            var snapshot = ReadSnapshot(document.RootElement);
            ValidateHeader(snapshot);
            if (snapshot.WarId != warId)
            {
                accepted = true;
                return; // Explicit newwar never inherits the old map's inventory.
            }
            var objectives = GetObjectives(mapId);
            Validate(snapshot, objectives); // All entries preflight before spawning or removing anything.
            _resourceFields.ValidateSnapshot(mapId, snapshot.Resources);
            _refineries.ValidateSnapshot(mapId, snapshot.Refineries);
            _factories.ValidateSnapshot(mapId, snapshot.Factories);
            ValidateVehicles(mapId, snapshot.Vehicles);
            var freshVehicles = VehicleEntities(mapId);
            var freshVehicleState = freshVehicles.ToDictionary(uid => uid,
                uid => ReadVehicle(uid, VehicleGround(mapId), vacant: true));
            _factories.StageSnapshot(mapId, snapshot.Factories, stagedFactoryClaims, restoringFactories);
            _refineries.StageSnapshot(mapId, snapshot.Refineries, stagedStacks, restoringRefineries);
            foreach (var entry in snapshot.Bases)
            {
                var replacement = _halls.StageRestoredObjective(new EntProtoId(entry.Prototype),
                    new TerritoryId(entry.TerritoryId),
                    entry.FactionId is { } faction ? new FactionId(faction) : null,
                    Transform(objectives[entry.TerritoryId]).Coordinates, new DamageSpecifier
                    {
                        DamageDict = entry.DamageHundredths.ToDictionary(
                            pair => new ProtoId<DamageTypePrototype>(pair.Key), pair => FixedPoint2.FromHundredths(pair.Value)),
                    }, staged);
                if (!Usable(replacement) || Transform(replacement).MapID != mapId ||
                    !_territories.Contains(new TerritoryId(entry.TerritoryId), Transform(replacement).Coordinates, includePaused: true))
                    throw new InvalidDataException("Restored objective is not live in its territory.");
                if (entry.ObjectiveKind == "hall")
                    _stockpiles.RestoreCounts(replacement, entry.Counts);
                else
                    _halls.RestoreRuinProgress(replacement, entry.RuinMaterialDeposited);
                if (!Usable(replacement) || !HasComp<DamageableComponent>(replacement) ||
                    !ReadDamage(replacement).OrderBy(pair => pair.Key)
                        .SequenceEqual(entry.DamageHundredths.OrderBy(pair => pair.Key)))
                    throw new InvalidDataException("Restored objective did not retain its exact live damage.");
            }
            _resourceFields.StageSnapshot(mapId, snapshot.Resources, stagedNodes);
            _refineries.ValidateStagedSnapshot(mapId, snapshot.Refineries, stagedStacks);
            _factories.ValidateStagedSnapshot(mapId, snapshot.Factories, stagedFactoryClaims);
            var resourcesCommitted = false;
            var basesCommitted = false;
            var machinesCommitted = false;
            StageVehicles(mapId, snapshot.Vehicles, freshVehicles, stagedVehicles, stagedVehicleCargo, ValidateOtherSlices);
            // Stage all paid claims before deleting fresh YAML entities. Nodes have no field ownership yet.
            if (!Usable(map) || staged.Any(uid => !Usable(uid)))
                throw new InvalidDataException("Restored objective or map was lost before commit.");
            _resourceFields.CommitSnapshot(mapId, snapshot.Resources, stagedNodes, ref commitStarted);
            resourcesCommitted = true;
            ValidateAllSlices();
            // Old resource callbacks can invalidate staged bases, and old base callbacks can invalidate resources.
            if (!Usable(map) || staged.Any(uid => !Usable(uid)))
                throw new InvalidDataException("Restored objective or map was lost during resource commit.");
            _factories.ValidateStagedSnapshot(mapId, snapshot.Factories, stagedFactoryClaims);
            foreach (var objective in objectives.Values)
            {
                _halls.DeleteObjective(objective);
                ValidateAllSlices();
            }
            if (!Usable(map) || staged.Any(uid => !Usable(uid)))
                throw new InvalidDataException("Restored objective or map was lost during base commit.");
            var restored = GetObjectives(mapId);
            basesCommitted = true;
            for (var i = 0; i < snapshot.Bases.Count; i++)
            {
                if (restored[snapshot.Bases[i].TerritoryId] != staged[i])
                    throw new InvalidDataException("Restored strategic objective set changed during commit.");
            }
            ValidateStagedBases(mapId, snapshot.Bases, staged);
            _resourceFields.ValidateStagedSnapshot(mapId, snapshot.Resources, stagedNodes, committed: true);
            _refineries.CommitSnapshot(mapId, snapshot.Refineries, stagedStacks);
            _factories.CommitSnapshot(mapId, snapshot.Factories, stagedFactoryClaims);
            machinesCommitted = true;
            ValidateAllSlices();
            CommitVehicles(mapId, snapshot.Vehicles, freshVehicles, stagedVehicles, stagedVehicleCargo,
                ValidateOtherSlices, ref commitStarted);
            // Both ledgers must still be exact after all native callbacks, before releasing either guard.
            ValidateAllSlices();
            accepted = true;

            void ValidateAllSlices()
            {
                ValidateOtherSlices();
                ValidateStagedVehicles(mapId, snapshot.Vehicles, freshVehicles, stagedVehicles, stagedVehicleCargo);
            }

            void ValidateOtherSlices()
            {
                if (!Usable(map))
                    throw new InvalidDataException("Strategic map was lost during native callbacks.");
                ValidateStagedBases(mapId, snapshot.Bases, staged);
                if (basesCommitted && !GetObjectives(mapId).Values.ToHashSet().SetEquals(staged))
                    throw new InvalidDataException("Restored strategic objective set changed during native callbacks.");
                _resourceFields.ValidateStagedSnapshot(mapId, snapshot.Resources, stagedNodes, committed: resourcesCommitted);
                _refineries.ValidateStagedSnapshot(mapId, snapshot.Refineries, stagedStacks, committed: machinesCommitted);
                _factories.ValidateStagedSnapshot(mapId, snapshot.Factories, stagedFactoryClaims, committed: machinesCommitted);
                foreach (var uid in freshVehicles)
                {
                    if (!SameVehicle(ReadVehicle(uid, VehicleGround(mapId), vacant: true), freshVehicleState[uid]))
                        throw new InvalidDataException("Fresh vehicle changed before replacement.");
                }
            }
        }
        catch
        {
            // Once originals were discarded, this is not a rollback to usable fresh YAML.
            // Abort this entire unaccepted map; retry must load a clean map from the unchanged disk claim.
            if (commitStarted)
                _failedMaps.Add(map);
            Exception? cleanupFailure = null;
            // Quarantine ALL slices before any synchronous deletion or restoration-guard release.
            Cleanup(() => QueueStrategicClaims(stagedFactoryClaims.Concat(stagedStacks).Concat(stagedVehicleCargo)
                .Concat(stagedVehicles).Concat(stagedNodes).Concat(staged)));
            // Clean each machine slice independently: a refinery failure cannot release factory goods.
            Cleanup(() => _factories.DeleteStagedClaims(stagedFactoryClaims));
            Cleanup(() => _refineries.DeleteStagedStacks(stagedStacks));
            Cleanup(() => DeleteVehicleClaims(stagedVehicles, stagedVehicleCargo));
            if (commitStarted)
                Cleanup(() => { if (!TerminatingOrDeleted(map)) Del(map); });
            else
            {
                Cleanup(() => _resourceFields.DeleteStagedNodes(stagedNodes));
                foreach (var objective in staged)
                    Cleanup(() => _halls.DeleteObjective(objective));
            }
            if (cleanupFailure != null)
            {
                _failedMaps.Add(map);
                Log.Warning($"Strategic restore cleanup failed; this map cannot be retried: {cleanupFailure}");
            }

            void Cleanup(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception e) { cleanupFailure ??= e; }
            }
            throw;
        }
        finally
        {
            _refineries.FinishSnapshotRestore(restoringRefineries);
            _factories.FinishSnapshotRestore(restoringFactories);
            foreach (var uid in stagedVehicles)
                _restoringVehicles.Remove(uid);
            if (accepted)
            {
                _loadedMap = map;
                _loadedWarId = warId;
            }
        }
    }

    private void OnCleanup(RoundRestartCleanupEvent args)
    {
        SaveLoadedMap();
    }

    private void SaveLoadedMap()
    {
        if (_loadedMap is not { } map || !Usable(map) || _ticker.CurrentPreset?.ID != "PersistentWar" ||
            !_resources.UserData.Exists(WarStateSystem.SavePath))
        {
            _loadedMap = null;
            return;
        }
        var mapId = Comp<MapComponent>(map).MapId;
        var objectives = GetObjectives(mapId);
        var bases = new List<WarBaseSnapshot>();
        foreach (var (territory, uid) in objectives)
        {
            var hall = TryComp<TownHallComponent>(uid, out var component);
            var counts = hall
                ? Comp<FrontlineStockpileComponent>(uid).Counts.ToDictionary(entry => entry.Key.Id, entry => entry.Value)
                : new Dictionary<string, int>();
            bases.Add(new WarBaseSnapshot(territory, hall ? "hall" : "ruin",
                MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Objective has no prototype."),
                hall ? component!.FactionId : null, counts)
            {
                DamageHundredths = ReadDamage(uid),
                RuinMaterialDeposited = hall ? 0 : Comp<TownHallRuinComponent>(uid).DepositedBasicMaterials,
            });
        }
        // StartNewWar may already have changed WarState. This map still belongs to its loaded war.
        var snapshot = new WarStrategicSnapshot(SnapshotVersion, _loadedWarId, bases)
        {
            Resources = _resourceFields.CaptureSnapshot(mapId),
            Refineries = _refineries.CaptureSnapshot(mapId),
            Factories = _factories.CaptureSnapshot(mapId),
            Vehicles = CaptureVehicles(mapId),
        };
        Validate(snapshot, objectives);
        // Propagate failure before native entity flush, retaining this map for a later retry.
        Save(snapshot);
        _loadedMap = null;
    }

    private void ValidateStagedBases(MapId mapId, List<WarBaseSnapshot> entries, List<EntityUid> staged)
    {
        if (staged.Count != entries.Count || staged.Distinct().Count() != staged.Count)
            throw new InvalidDataException("Restored strategic base count changed.");
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var uid = staged[i];
            if (!Usable(uid) || Transform(uid).MapID != mapId ||
                !_territories.Contains(new TerritoryId(entry.TerritoryId), Transform(uid).Coordinates, includePaused: true) ||
                MetaData(uid).EntityPrototype?.ID != entry.Prototype || !HasComp<DamageableComponent>(uid) ||
                !ReadDamage(uid).OrderBy(pair => pair.Key).SequenceEqual(entry.DamageHundredths.OrderBy(pair => pair.Key)) ||
                (entry.ObjectiveKind == "hall"
                    ? !TryComp<TownHallComponent>(uid, out var hall) || hall.TerritoryId != entry.TerritoryId ||
                      hall.FactionId != entry.FactionId || !TryComp<FrontlineStockpileComponent>(uid, out var stockpile) ||
                      !stockpile.Counts.OrderBy(pair => pair.Key.Id)
                          .Select(pair => new KeyValuePair<string, int>(pair.Key.Id, pair.Value))
                          .SequenceEqual(entry.Counts.OrderBy(pair => pair.Key))
                    : !TryComp<TownHallRuinComponent>(uid, out var ruin) || ruin.TerritoryId != entry.TerritoryId ||
                      ruin.DepositedBasicMaterials != entry.RuinMaterialDeposited))
                throw new InvalidDataException("Restored strategic base changed during native callbacks.");
        }
    }

    // Exact damage is persistence data, not a player-facing health measurement.
#pragma warning disable CS0618
    private Dictionary<string, int> ReadDamage(EntityUid uid) =>
        _damage.GetAllDamage(uid).DamageDict.ToDictionary(pair => pair.Key.Id, pair => pair.Value.Value);
#pragma warning restore CS0618

    private void Save(WarStrategicSnapshot snapshot)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var data = _resources.UserData;
        using (var stream = data.Open(TemporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush();
        }
        if (data.Exists(SavePath))
        {
            var validSyntax = true;
            try
            {
                using var primary = data.OpenRead(SavePath);
                using var document = JsonDocument.Parse(primary);
            }
            catch (JsonException)
            {
                validSyntax = false;
            }
            if (validSyntax)
            {
                data.Delete(BackupPath);
                data.Rename(SavePath, BackupPath); // Native Rename does not overwrite.
            }
            else
            {
                // A recovered map must not rotate the corrupt primary over its committed backup.
                data.Delete(SavePath);
            }
        }
        try
        {
            data.Rename(TemporaryPath, SavePath);
        }
        catch
        {
            if (!data.Exists(SavePath) && data.Exists(BackupPath))
                data.Rename(BackupPath, SavePath);
            throw;
        }
    }
}
