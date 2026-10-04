using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Server.GameTicking;
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
public sealed record WarStrategicSnapshot(int SnapshotVersion, int WarId, List<WarBaseSnapshot> Bases);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarBaseSnapshot(string TerritoryId, string ObjectiveKind, string Prototype,
    string? FactionId, Dictionary<string, int> Counts);

/// <summary>Technical-restart persistence for objectives and virtual stockpiles only.</summary>
public sealed partial class WarStrategicSnapshotSystem : EntitySystem
{
    public static readonly ResPath SavePath = new("/persistent-war-strategic.json");
    public static readonly ResPath TemporaryPath = new("/persistent-war-strategic.json.tmp");
    public static readonly ResPath BackupPath = new("/persistent-war-strategic.json.bak");

    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private TownHallSystem _halls = default!;
    [Dependency] private FrontlineStockpileSystem _stockpiles = default!;

    private EntityUid? _loadedMap;
    private int _loadedWarId;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnCleanup);
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
                !_territories.Contains(new TerritoryId(id), xform.Coordinates) || !result.TryAdd(id, uid))
                throw new InvalidDataException("Invalid or duplicate strategic objective.");
        }
        var halls = EntityQueryEnumerator<TownHallComponent, TransformComponent>();
        while (halls.MoveNext(out var uid, out var hall, out var xform))
            Add(uid, hall.TerritoryId, xform);
        var ruins = EntityQueryEnumerator<TownHallRuinComponent, TransformComponent>();
        while (ruins.MoveNext(out var uid, out var ruin, out var xform))
            Add(uid, ruin.TerritoryId, xform);
        if (!result.Keys.ToHashSet().SetEquals(_territories.GetTerritories(mapId).Select(id => id.Id)))
            throw new InvalidDataException("Strategic objectives do not cover the loaded territories.");
        return result;
    }

    private void Validate(WarStrategicSnapshot snapshot, Dictionary<string, EntityUid> objectives)
    {
        if (snapshot.SnapshotVersion != 1 || snapshot.WarId <= 0 || snapshot.Bases == null ||
            snapshot.Bases.Count != objectives.Count)
            throw new InvalidDataException("Invalid strategic snapshot header or objective count.");
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
            var ruin = prototype.HasComp<TownHallRuinComponent>(EntityManager.ComponentFactory);
            if (entry.ObjectiveKind == "hall")
            {
                if (!hall || ruin || string.IsNullOrWhiteSpace(entry.FactionId) ||
                    !_prototypes.HasIndex<FrontlineFactionPrototype>(entry.FactionId) ||
                    hallComponent!.FactionId != entry.FactionId ||
                    !prototype.HasComp<FrontlineStockpileComponent>(EntityManager.ComponentFactory))
                    throw new InvalidDataException("Invalid strategic hall faction or components.");
            }
            else if (entry.ObjectiveKind != "ruin" || !ruin || hall || entry.FactionId != null || entry.Counts.Count != 0)
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
        if (_loadedMap == map)
            return;
        var mapId = Comp<MapComponent>(map).MapId;
        var staged = new List<EntityUid>();
        var accepted = true;
        try
        {
            var data = _resources.UserData;
            // A missing primary after rotation means the backup is the last committed snapshot.
            var path = data.Exists(SavePath) ? SavePath : BackupPath;
            if (!data.Exists(path))
                return;
            using var stream = data.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            RejectDuplicateKeys(document.RootElement);
            var snapshot = document.RootElement.Deserialize<WarStrategicSnapshot>() ??
                           throw new InvalidDataException("Empty strategic snapshot.");
            if (snapshot.WarId != warId)
                return; // Explicit newwar never inherits the old map's inventory.
            var objectives = GetObjectives(mapId);
            Validate(snapshot, objectives); // All entries preflight before spawning or removing anything.
            foreach (var entry in snapshot.Bases)
            {
                var replacement = _halls.StageRestoredObjective(new EntProtoId(entry.Prototype),
                    new TerritoryId(entry.TerritoryId),
                    entry.FactionId is { } faction ? new FactionId(faction) : null,
                    Transform(objectives[entry.TerritoryId]).Coordinates);
                staged.Add(replacement);
                if (!Usable(replacement) || Transform(replacement).MapID != mapId ||
                    !_territories.Contains(new TerritoryId(entry.TerritoryId), Transform(replacement).Coordinates))
                    throw new InvalidDataException("Restored objective is not live in its territory.");
                if (entry.ObjectiveKind == "hall")
                    _stockpiles.RestoreCounts(replacement, entry.Counts);
            }
            // All replacements are ready; fresh YAML objectives remain untouched on preflight/staging failure.
            staged.Clear();
            foreach (var objective in objectives.Values)
                _halls.DeleteObjective(objective);
        }
        catch (Exception e)
        {
            accepted = false;
            // Never replace an unreadable paid inventory with fresh campaign defaults.
            Log.Error($"Strategic snapshot refused; aborting campaign startup: {e}");
            throw;
        }
        finally
        {
            foreach (var objective in staged)
                _halls.DeleteObjective(objective);
            if (accepted)
            {
                _loadedMap = map;
                _loadedWarId = warId;
            }
        }
    }

    private void OnCleanup(RoundRestartCleanupEvent args)
    {
        try
        {
            if (_loadedMap is not { } map || !Usable(map) || _ticker.CurrentPreset?.ID != "PersistentWar" ||
                !_resources.UserData.Exists(WarStateSystem.SavePath))
                return;
            var objectives = GetObjectives(Comp<MapComponent>(map).MapId);
            var bases = new List<WarBaseSnapshot>();
            foreach (var (territory, uid) in objectives)
            {
                var hall = TryComp<TownHallComponent>(uid, out var component);
                var counts = hall
                    ? Comp<FrontlineStockpileComponent>(uid).Counts.ToDictionary(entry => entry.Key.Id, entry => entry.Value)
                    : new Dictionary<string, int>();
                bases.Add(new WarBaseSnapshot(territory, hall ? "hall" : "ruin",
                    MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Objective has no prototype."),
                    hall ? component!.FactionId : null, counts));
            }
            // StartNewWar may already have changed WarState. This map still belongs to its loaded war.
            var snapshot = new WarStrategicSnapshot(1, _loadedWarId, bases);
            Validate(snapshot, objectives);
            Save(snapshot);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save strategic snapshot; previous committed file retained: {e}");
        }
        finally
        {
            _loadedMap = null;
        }
    }

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
