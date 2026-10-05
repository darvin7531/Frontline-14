using System.IO;
using System.Linq;
using Content.Server.Stack;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.War;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.War;

public sealed partial class FrontlineResourceFieldSystem : EntitySystem
{
    private static readonly ProtoId<TagPrototype> PickaxeTag = "Pickaxe";
    private readonly HashSet<EntityUid> _extractingNodes = [];

    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private PersistentWarMapValidatorSystem _mapValidator = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineResourceFieldComponent, EntityTerminatingEvent>(OnFieldTerminating);
        SubscribeLocalEvent<FrontlineResourceFieldComponent, ExaminedEvent>(OnFieldExamined);
        SubscribeLocalEvent<FrontlineResourceNodeComponent, EntityTerminatingEvent>(OnNodeTerminating);
        SubscribeLocalEvent<FrontlineResourceNodeComponent, MapInitEvent>(OnNodeMapInit);
        SubscribeLocalEvent<FrontlineResourceNodeComponent, AfterInteractUsingEvent>(OnNodeInteract);
        SubscribeLocalEvent<FrontlineResourceNodeComponent, FrontlineResourceExtractionDoAfterEvent>(OnExtractionComplete);
    }

    private void OnFieldExamined(Entity<FrontlineResourceFieldComponent> field, ref ExaminedEvent args)
    {
        var remaining = field.Comp.State is FrontlineResourceFieldState.Depleted or FrontlineResourceFieldState.Replenishing
            ? Math.Max(0, (int) Math.Ceiling((field.Comp.NextReplenishment - _timing.CurTime).TotalSeconds))
            : 0;
        args.PushMarkup(Loc.GetString("frontline-resource-field-examine",
            ("state", field.Comp.State),
            ("reserve", field.Comp.RemainingReserveNodes),
            ("active", field.Comp.ActiveNodes.Count),
            ("seconds", remaining)));
    }

    private void OnFieldTerminating(Entity<FrontlineResourceFieldComponent> field, ref EntityTerminatingEvent args)
    {
        foreach (var node in field.Comp.ActiveNodes)
        {
            if (Exists(node))
                QueueDel(node);
        }
    }

    private void OnNodeMapInit(Entity<FrontlineResourceNodeComponent> node, ref MapInitEvent args)
    {
        if (node.Comp.RemainingYield < 0)
            node.Comp.RemainingYield = node.Comp.MaxYield;
    }

    private void OnNodeInteract(Entity<FrontlineResourceNodeComponent> node, ref AfterInteractUsingEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        args.Handled = TryStartExtraction(node, args.User, args.Used);
    }

    public bool TryStartExtraction(EntityUid node, EntityUid user, EntityUid tool)
    {
        if (!TryComp<FrontlineResourceNodeComponent>(node, out var nodeComp) || nodeComp.RemainingYield <= 0 ||
            !_tags.HasTag(tool, PickaxeTag) || !_extractingNodes.Add(node))
            return false;

        var started = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, nodeComp.ExtractionTime,
            new FrontlineResourceExtractionDoAfterEvent(), node, target: node, used: tool)
        {
            BreakOnMove = true,
            NeedHand = true,
        });
        if (!started)
            _extractingNodes.Remove(node);
        return started;
    }

    private void OnExtractionComplete(Entity<FrontlineResourceNodeComponent> node,
        ref FrontlineResourceExtractionDoAfterEvent args)
    {
        _extractingNodes.Remove(node);
        if (args.Cancelled || args.Used is not { } tool || node.Comp.RemainingYield <= 0 ||
            !_tags.HasTag(tool, PickaxeTag) || !_interaction.InRangeUnobstructed(args.User, node.Owner))
            return;

        var amount = Math.Min(node.Comp.HarvestAmount, node.Comp.RemainingYield);
        node.Comp.RemainingYield -= amount;
        var output = _stack.SpawnAtPosition(amount, node.Comp.Output, Transform(node).Coordinates);
        _stack.TryMergeToContacts(output);
        foreach (var bonus in node.Comp.BonusDrops)
        {
            if (!_random.Prob(bonus.Chance))
                continue;

            var bonusOutput = _stack.SpawnAtPosition(_random.Next(bonus.MinAmount, bonus.MaxAmount + 1),
                bonus.Output,
                Transform(node).Coordinates);
            _stack.TryMergeToContacts(bonusOutput);
        }

        if (node.Comp.RemainingYield == 0)
            Del(node);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<FrontlineResourceFieldComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var field, out var transform))
        {
            if (!field.CacheInitialized)
            {
                RebuildFieldCache((uid, field), transform.MapID);
                field.CacheInitialized = true;
            }

            var canSpawn = false;
            if (!field.FieldInitialized)
            {
                field.RemainingReserveNodes = field.MaxReserveNodes;
                field.State = FrontlineResourceFieldState.Active;
                field.FieldInitialized = true;
                canSpawn = true;
            }
            else if (field.State == FrontlineResourceFieldState.Depleted)
            {
                field.State = FrontlineResourceFieldState.Replenishing;
                continue;
            }
            else if (field.State == FrontlineResourceFieldState.Replenishing)
            {
                if (_timing.CurTime < field.NextReplenishment)
                    continue;

                field.RemainingReserveNodes = field.MaxReserveNodes;
                field.State = FrontlineResourceFieldState.Active;
                canSpawn = true;
            }
            else if (field.ActiveNodes.Count >= field.MaxActiveNodes || field.RemainingReserveNodes <= 0)
                continue;
            else if (field.NextReplacement <= _timing.CurTime)
                canSpawn = true;

            if (!canSpawn)
                continue;

            var slots = EntityQueryEnumerator<FrontlineResourceSpawnPointComponent, TransformComponent>();
            while (slots.MoveNext(out var slotUid, out var slot, out var slotTransform))
            {
                if (field.ActiveNodes.Count >= field.MaxActiveNodes || field.RemainingReserveNodes == 0)
                    break;

                if (slot.FieldId != field.FieldId || slotTransform.MapID != transform.MapID ||
                    SlotOccupied(field, slotUid))
                    continue;

                SpawnNode((uid, field), slotUid, slotTransform.Coordinates);
            }

            field.NextReplacement = TimeSpan.Zero;
        }
    }

    private void OnNodeTerminating(Entity<FrontlineResourceNodeComponent> node, ref EntityTerminatingEvent args)
    {
        _extractingNodes.Remove(node);
        if (!TryComp<FrontlineResourceFieldComponent>(node.Comp.Field, out var field) ||
            !field.ActiveNodes.Remove(node))
            return;

        var coordinates = Transform(node).Coordinates;
        if (TerminatingOrDeleted(coordinates.EntityId))
            return;

        if (field.RemainingReserveNodes == 0 && field.ActiveNodes.Count == 0)
        {
            field.State = FrontlineResourceFieldState.Depleted;
            field.NextReplenishment = _timing.CurTime + field.ReplenishmentDelay;
            return;
        }

        if (field.RemainingReserveNodes > 0 && field.ActiveNodes.Count < field.MaxActiveNodes)
            field.NextReplacement = _timing.CurTime + field.ReplacementDelay;
    }

    private void RebuildFieldCache(Entity<FrontlineResourceFieldComponent> field, MapId mapId)
    {
        field.Comp.ActiveNodes.Clear();
        var nodes = EntityQueryEnumerator<FrontlineResourceNodeComponent, TransformComponent>();
        while (nodes.MoveNext(out var nodeUid, out var node, out var nodeTransform))
        {
            if (node.FieldId != field.Comp.FieldId || nodeTransform.MapID != mapId)
                continue;

            node.Field = field;
            node.SpawnPoint = EntityUid.Invalid;
            field.Comp.ActiveNodes.Add(nodeUid);

            var slots = EntityQueryEnumerator<FrontlineResourceSpawnPointComponent, TransformComponent>();
            while (slots.MoveNext(out var slotUid, out var slot, out var slotTransform))
            {
                if (slot.FieldId == field.Comp.FieldId && slotTransform.MapID == mapId &&
                    (string.IsNullOrWhiteSpace(node.SlotId)
                        ? slotTransform.ParentUid == nodeTransform.ParentUid && slotTransform.LocalPosition == nodeTransform.LocalPosition
                        : slot.SlotId == node.SlotId))
                {
                    node.SpawnPoint = slotUid;
                    node.SlotId = slot.SlotId;
                    break;
                }
            }
        }
    }

    private bool SlotOccupied(FrontlineResourceFieldComponent field, EntityUid slot)
    {
        foreach (var node in field.ActiveNodes)
        {
            if (TryComp<FrontlineResourceNodeComponent>(node, out var nodeComp) && nodeComp.SpawnPoint == slot)
                return true;
        }

        return false;
    }

    private void SpawnNode(Entity<FrontlineResourceFieldComponent> field, EntityUid spawnPoint,
        EntityCoordinates coordinates)
    {
        var node = Spawn(field.Comp.PrimaryNodePrototype, coordinates);
        var nodeComp = Comp<FrontlineResourceNodeComponent>(node);
        nodeComp.FieldId = field.Comp.FieldId;
        nodeComp.SlotId = Comp<FrontlineResourceSpawnPointComponent>(spawnPoint).SlotId;
        nodeComp.Field = field;
        nodeComp.SpawnPoint = spawnPoint;
        field.Comp.ActiveNodes.Add(node);
        field.Comp.RemainingReserveNodes--;
    }

    private bool Usable(EntityUid uid) => !TerminatingOrDeleted(uid) && !EntityManager.IsQueuedForDeletion(uid);

    private Dictionary<string, Entity<FrontlineResourceFieldComponent>> GetFields(MapId mapId)
    {
        var fields = new Dictionary<string, Entity<FrontlineResourceFieldComponent>>();
        var query = AllEntityQuery<FrontlineResourceFieldComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var field, out var transform))
        {
            if (transform.MapID != mapId)
                continue;
            // A pending controller still owns its reserve/cooldown; omission would mint fresh state.
            if (!Usable(uid))
                throw new InvalidDataException("Resource field deletion must settle before persistence.");
            fields.Add(field.FieldId, (uid, field));
        }
        return fields;
    }

    private Dictionary<(string FieldId, string SlotId), EntityUid> GetSlots(MapId mapId)
    {
        var slots = new Dictionary<(string, string), EntityUid>();
        var query = AllEntityQuery<FrontlineResourceSpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var slot, out var transform))
        {
            if (transform.MapID == mapId && Usable(uid))
                slots.Add((slot.FieldId, slot.SlotId), uid);
        }
        return slots;
    }

    /// <summary>Settled strategic state only; ownership, extraction locks and UIDs stay runtime-only.</summary>
    public List<WarResourceFieldSnapshot> CaptureSnapshot(MapId mapId)
    {
        var result = new List<WarResourceFieldSnapshot>();
        var nodes = new Dictionary<string, List<WarResourceNodeSnapshot>>();
        var query = AllEntityQuery<FrontlineResourceNodeComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var node, out var transform))
        {
            if (transform.MapID != mapId || TerminatingOrDeleted(uid))
                continue;
            if (string.IsNullOrWhiteSpace(node.FieldId))
                throw new InvalidDataException("Resource node has no field identity.");
            // Pending termination has not scheduled its replacement/depletion deadline yet.
            if (EntityManager.IsQueuedForDeletion(uid))
                throw new InvalidDataException("Resource node deletion must settle before capture.");
            if (!nodes.TryGetValue(node.FieldId, out var entries))
                nodes.Add(node.FieldId, entries = new());
            entries.Add(new WarResourceNodeSnapshot(node.SlotId,
                MetaData(uid).EntityPrototype?.ID ?? throw new InvalidDataException("Resource node has no prototype."),
                node.RemainingYield));
        }
        foreach (var (id, field) in GetFields(mapId))
        {
            var now = _timing.CurTime - _metadata.GetPauseTime(field.Owner);
            result.Add(new WarResourceFieldSnapshot(id, field.Comp.RemainingReserveNodes,
                field.Comp.FieldInitialized, field.Comp.State,
                Math.Max(0, (field.Comp.NextReplacement - now).Ticks),
                Math.Max(0, (field.Comp.NextReplenishment - now).Ticks),
                nodes.Remove(id, out var entries) ? entries : new()));
        }
        if (nodes.Count != 0)
            throw new InvalidDataException("Resource nodes reference an unknown field.");
        ValidateSnapshot(mapId, result);
        return result;
    }

    internal void ValidateSnapshot(MapId mapId, List<WarResourceFieldSnapshot> entries)
    {
        var errors = new List<string>();
        _mapValidator.ValidateResourceFields(mapId, errors);
        if (errors.Count != 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var fields = GetFields(mapId);
        var slots = GetSlots(mapId);
        if (entries.Count != fields.Count)
            throw new InvalidDataException("Resource snapshot does not cover every map field.");
        var seen = new HashSet<string>();
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.FieldId) || !seen.Add(entry.FieldId) ||
                !fields.TryGetValue(entry.FieldId, out var field) || entry.Nodes == null)
                throw new InvalidDataException("Invalid or unknown resource field.");
            var config = field.Comp;
            if (entry.RemainingReserveNodes < 0 || entry.RemainingReserveNodes > config.MaxReserveNodes ||
                entry.Nodes.Count > config.MaxActiveNodes ||
                (long) entry.RemainingReserveNodes + entry.Nodes.Count > config.MaxReserveNodes ||
                !Enum.IsDefined(entry.State) || entry.ReplacementRemainingTicks < 0 || entry.ReplenishmentRemainingTicks < 0 ||
                entry.ReplacementRemainingTicks > config.ReplacementDelay.Ticks ||
                entry.ReplenishmentRemainingTicks > config.ReplenishmentDelay.Ticks ||
                entry.ReplacementRemainingTicks > long.MaxValue - _timing.CurTime.Ticks ||
                entry.ReplenishmentRemainingTicks > long.MaxValue - _timing.CurTime.Ticks ||
                (!entry.FieldInitialized && (entry.RemainingReserveNodes != 0 || entry.Nodes.Count != 0 ||
                    entry.State != FrontlineResourceFieldState.Active || entry.ReplacementRemainingTicks != 0 ||
                    entry.ReplenishmentRemainingTicks != 0)) ||
                (entry.State == FrontlineResourceFieldState.Active && (entry.ReplenishmentRemainingTicks != 0 ||
                    entry.FieldInitialized && entry.RemainingReserveNodes == 0 && entry.Nodes.Count == 0)) ||
                (entry.State != FrontlineResourceFieldState.Active &&
                    (entry.RemainingReserveNodes != 0 || entry.Nodes.Count != 0 || entry.ReplacementRemainingTicks != 0)))
                throw new InvalidDataException("Invalid resource reserve, state or remaining deadlines.");
            var occupied = new HashSet<string>();
            foreach (var node in entry.Nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.SlotId) || !occupied.Add(node.SlotId) ||
                    !slots.ContainsKey((entry.FieldId, node.SlotId)) || string.IsNullOrWhiteSpace(node.Prototype))
                    throw new InvalidDataException("Invalid or unknown resource slot.");
                var prototype = new EntProtoId(node.Prototype);
                _mapValidator.ValidateResourceNodePrototype(entry.FieldId, prototype, "restored", errors);
                if (errors.Count != 0 || !_prototypes.TryIndex(prototype, out var entity) ||
                    !entity.TryGetComponent<FrontlineResourceNodeComponent>(out var component, EntityManager.ComponentFactory) ||
                    node.RemainingYield < 0 || node.RemainingYield > component.MaxYield)
                    throw new InvalidDataException("Invalid resource node prototype or yield.");
            }
        }
    }

    // All stages remain detached: rollback cannot start a field's replacement/replenishment timer.
    internal void StageSnapshot(MapId mapId, List<WarResourceFieldSnapshot> entries, List<EntityUid> staged)
    {
        var slots = GetSlots(mapId);
        foreach (var field in entries)
        foreach (var entry in field.Nodes)
        {
            var slot = slots[(field.FieldId, entry.SlotId)];
            var uid = Spawn(new EntProtoId(entry.Prototype), Transform(slot).Coordinates);
            staged.Add(uid);
            var node = Comp<FrontlineResourceNodeComponent>(uid);
            node.Field = EntityUid.Invalid;
            node.FieldId = string.Empty;
            node.SpawnPoint = EntityUid.Invalid;
            node.SlotId = entry.SlotId;
            node.RemainingYield = entry.RemainingYield; // Zero is exhausted, not an initialization sentinel.
            if (!Usable(uid) || Transform(uid).MapID != mapId ||
                node.RemainingYield > node.MaxYield ||
                Transform(uid).ParentUid != Transform(slot).ParentUid ||
                Transform(uid).LocalPosition != Transform(slot).LocalPosition)
                throw new InvalidDataException("Restored resource node is not live at its slot.");
        }
    }

    internal void DeleteStagedNodes(List<EntityUid> staged)
    {
        foreach (var uid in staged)
        {
            if (!TerminatingOrDeleted(uid))
                Del(uid);
        }
    }

    internal void ValidateStagedSnapshot(MapId mapId, List<WarResourceFieldSnapshot> entries,
        List<EntityUid> staged, bool committed = false)
    {
        var fields = GetFields(mapId);
        var slots = GetSlots(mapId);
        if (staged.Any(uid => !Usable(uid)) || fields.Count != entries.Count ||
            staged.Count != entries.Sum(entry => entry.Nodes.Count) ||
            entries.Any(entry => !fields.ContainsKey(entry.FieldId) ||
                entry.Nodes.Any(node => !slots.ContainsKey((entry.FieldId, node.SlotId)))))
            throw new InvalidDataException("Resource entities were lost during restore.");
        if (committed)
        {
            // Callbacks can mint detached or unknown-field nodes outside the rebuilt ownership caches.
            var mapNodes = new HashSet<EntityUid>();
            var query = AllEntityQuery<FrontlineResourceNodeComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var transform))
            {
                if (transform.MapID == mapId)
                    mapNodes.Add(uid);
            }
            if (!mapNodes.SetEquals(staged) || mapNodes.Count != staged.Count)
                throw new InvalidDataException("Restored map resource nodes do not match the staged claims.");
        }
        var index = 0;
        foreach (var field in entries)
        {
            var controller = fields[field.FieldId];
            if (committed && (controller.Comp.RemainingReserveNodes != field.RemainingReserveNodes ||
                controller.Comp.FieldInitialized != field.FieldInitialized || controller.Comp.State != field.State ||
                !controller.Comp.CacheInitialized ||
                controller.Comp.ActiveNodes.Count != field.Nodes.Count ||
                controller.Comp.NextReplacement != (field.ReplacementRemainingTicks == 0 ? TimeSpan.Zero :
                    _timing.CurTime + TimeSpan.FromTicks(field.ReplacementRemainingTicks)) ||
                controller.Comp.NextReplenishment != (field.ReplenishmentRemainingTicks == 0 ? TimeSpan.Zero :
                    _timing.CurTime + TimeSpan.FromTicks(field.ReplenishmentRemainingTicks))))
                throw new InvalidDataException("Restored resource controller changed during commit.");
            foreach (var saved in field.Nodes)
            {
                var uid = staged[index++];
                var slot = slots[(field.FieldId, saved.SlotId)];
                var node = Comp<FrontlineResourceNodeComponent>(uid);
                if (Transform(uid).MapID != mapId || Transform(uid).Coordinates != Transform(slot).Coordinates ||
                    MetaData(uid).EntityPrototype?.ID != saved.Prototype || node.RemainingYield != saved.RemainingYield ||
                    node.SlotId != saved.SlotId ||
                    (!committed && (node.Field != EntityUid.Invalid || !string.IsNullOrEmpty(node.FieldId) ||
                        node.SpawnPoint != EntityUid.Invalid)) ||
                    (committed && (node.Field != controller.Owner || node.FieldId != field.FieldId ||
                        node.SpawnPoint != slot || !controller.Comp.ActiveNodes.Contains(uid))))
                    throw new InvalidDataException("Restored resource node changed during commit.");
            }
        }
    }

    internal void CommitSnapshot(MapId mapId, List<WarResourceFieldSnapshot> entries, List<EntityUid> staged,
        ref bool commitStarted)
    {
        ValidateStagedSnapshot(mapId, entries, staged);
        var fields = GetFields(mapId);
        var slots = GetSlots(mapId);
        // From here, rollback of detached stages cannot recover the discarded YAML claims.
        commitStarted = true;
        var old = new List<EntityUid>();
        var query = AllEntityQuery<FrontlineResourceNodeComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var node, out var transform))
        {
            if (transform.MapID != mapId || !fields.ContainsKey(node.FieldId))
                continue;
            // Includes queued nodes; their eventual termination must not mutate the restored field.
            node.Field = EntityUid.Invalid;
            node.FieldId = string.Empty;
            node.SpawnPoint = EntityUid.Invalid;
            _extractingNodes.Remove(uid);
            old.Add(uid);
        }
        DeleteStagedNodes(old);
        // Del runs native termination/shutdown callbacks synchronously. Preflight alone is not a commit check.
        ValidateStagedSnapshot(mapId, entries, staged);
        if (fields.Values.Any(field => !Usable(field.Owner)) || slots.Values.Any(uid => !Usable(uid)))
            throw new InvalidDataException("Resource controller or slot was lost during old-node deletion.");
        foreach (var field in fields.Values)
            field.Comp.ActiveNodes.Clear();
        var index = 0;
        foreach (var entry in entries)
        {
            var field = fields[entry.FieldId];
            field.Comp.RemainingReserveNodes = entry.RemainingReserveNodes;
            field.Comp.FieldInitialized = entry.FieldInitialized;
            field.Comp.State = entry.State;
            field.Comp.NextReplacement = entry.ReplacementRemainingTicks == 0 ? TimeSpan.Zero :
                _timing.CurTime + TimeSpan.FromTicks(entry.ReplacementRemainingTicks);
            field.Comp.NextReplenishment = entry.ReplenishmentRemainingTicks == 0 ? TimeSpan.Zero :
                _timing.CurTime + TimeSpan.FromTicks(entry.ReplenishmentRemainingTicks);
            foreach (var saved in entry.Nodes)
            {
                var uid = staged[index++];
                var node = Comp<FrontlineResourceNodeComponent>(uid);
                node.FieldId = entry.FieldId;
                node.SlotId = saved.SlotId;
                node.Field = field.Owner;
                node.SpawnPoint = slots[(entry.FieldId, saved.SlotId)];
                field.Comp.ActiveNodes.Add(uid);
            }
            field.Comp.CacheInitialized = true;
        }
        ValidateStagedSnapshot(mapId, entries, staged, committed: true);
    }
}