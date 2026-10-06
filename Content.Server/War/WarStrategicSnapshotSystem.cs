using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Server.GameTicking;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
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
    [property: JsonRequired] long RemainingTicks);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarFactoryCrateSnapshot(
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] string Product,
    [property: JsonRequired] int Amount);

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
    [property: JsonRequired] long RemainingTicks);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarRefineryStackSnapshot(
    [property: JsonRequired] string StackId,
    [property: JsonRequired] string Prototype,
    [property: JsonRequired] int Count);

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

/// <summary>Technical-restart persistence for objectives, stockpiles, resource fields, refineries and factories.</summary>
public sealed partial class WarStrategicSnapshotSystem : EntitySystem
{
    // Earlier versions omitted paid machine claims; never accept omitted state as fresh defaults.
    public const int SnapshotVersion = 5;
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
            snapshot.Bases == null || snapshot.Resources == null || snapshot.Refineries == null || snapshot.Factories == null)
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
            var snapshot = document.RootElement.Deserialize<WarStrategicSnapshot>() ??
                           throw new InvalidDataException("Empty strategic snapshot.");
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
            // Stage all paid claims before deleting fresh YAML entities. Nodes have no field ownership yet.
            if (!Usable(map) || staged.Any(uid => !Usable(uid)))
                throw new InvalidDataException("Restored objective or map was lost before commit.");
            _resourceFields.CommitSnapshot(mapId, snapshot.Resources, stagedNodes, ref commitStarted);
            // Old resource callbacks can invalidate staged bases, and old base callbacks can invalidate resources.
            if (!Usable(map) || staged.Any(uid => !Usable(uid)))
                throw new InvalidDataException("Restored objective or map was lost during resource commit.");
            _factories.ValidateStagedSnapshot(mapId, snapshot.Factories, stagedFactoryClaims);
            foreach (var objective in objectives.Values)
            {
                _halls.DeleteObjective(objective);
                _factories.ValidateStagedSnapshot(mapId, snapshot.Factories, stagedFactoryClaims);
            }
            if (!Usable(map) || staged.Any(uid => !Usable(uid)))
                throw new InvalidDataException("Restored objective or map was lost during base commit.");
            var restored = GetObjectives(mapId);
            for (var i = 0; i < snapshot.Bases.Count; i++)
            {
                var entry = snapshot.Bases[i];
                var uid = staged[i];
                if (restored[entry.TerritoryId] != uid || MetaData(uid).EntityPrototype?.ID != entry.Prototype ||
                    !ReadDamage(uid).OrderBy(pair => pair.Key)
                        .SequenceEqual(entry.DamageHundredths.OrderBy(pair => pair.Key)) ||
                    (entry.ObjectiveKind == "hall"
                        ? Comp<TownHallComponent>(uid).FactionId != entry.FactionId ||
                          !Comp<FrontlineStockpileComponent>(uid).Counts.OrderBy(pair => pair.Key.Id)
                              .Select(pair => new KeyValuePair<string, int>(pair.Key.Id, pair.Value))
                              .SequenceEqual(entry.Counts.OrderBy(pair => pair.Key))
                        : Comp<TownHallRuinComponent>(uid).DepositedBasicMaterials != entry.RuinMaterialDeposited))
                    throw new InvalidDataException("Restored strategic base changed during commit.");
            }
            _resourceFields.ValidateStagedSnapshot(mapId, snapshot.Resources, stagedNodes, committed: true);
            _refineries.CommitSnapshot(mapId, snapshot.Refineries, stagedStacks);
            _factories.CommitSnapshot(mapId, snapshot.Factories, stagedFactoryClaims);
            // Both ledgers must still be exact after all native callbacks, before releasing either guard.
            _refineries.ValidateStagedSnapshot(mapId, snapshot.Refineries, stagedStacks, committed: true);
            accepted = true;
        }
        catch
        {
            // Once originals were discarded, this is not a rollback to usable fresh YAML.
            // Abort this entire unaccepted map; retry must load a clean map from the unchanged disk claim.
            if (commitStarted)
                _failedMaps.Add(map);
            Exception? cleanupFailure = null;
            // Clean each machine slice independently: a refinery failure cannot release factory goods.
            Cleanup(() => _factories.DeleteStagedClaims(stagedFactoryClaims));
            Cleanup(() => _refineries.DeleteStagedStacks(stagedStacks));
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
        };
        Validate(snapshot, objectives);
        // Propagate failure before native entity flush, retaining this map for a later retry.
        Save(snapshot);
        _loadedMap = null;
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
            data.Delete(BackupPath);
            data.Rename(SavePath, BackupPath); // Native Rename does not overwrite.
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
