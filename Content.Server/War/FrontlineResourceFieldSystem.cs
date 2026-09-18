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

    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineResourceFieldComponent, EntityTerminatingEvent>(OnFieldTerminating);
        SubscribeLocalEvent<FrontlineResourceFieldComponent, ExaminedEvent>(OnFieldExamined);
        SubscribeLocalEvent<FrontlineResourceNodeComponent, EntityTerminatingEvent>(OnNodeTerminating);
        SubscribeLocalEvent<FrontlineResourceNodeComponent, MapInitEvent>(OnNodeMapInit);
        SubscribeLocalEvent<FrontlineResourceNodeComponent, AfterInteractEvent>(OnNodeInteract);
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
        node.Comp.RemainingYield = node.Comp.MaxYield;
    }

    private void OnNodeInteract(Entity<FrontlineResourceNodeComponent> node, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        args.Handled = TryStartExtraction(node, args.User, args.Used);
    }

    public bool TryStartExtraction(EntityUid node, EntityUid user, EntityUid tool)
    {
        if (!TryComp<FrontlineResourceNodeComponent>(node, out var nodeComp) || nodeComp.RemainingYield <= 0 ||
            !_tags.HasTag(tool, PickaxeTag))
            return false;

        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, nodeComp.ExtractionTime,
            new FrontlineResourceExtractionDoAfterEvent(), node, target: node, used: tool)
        {
            BreakOnMove = true,
            NeedHand = true,
        });
    }

    private void OnExtractionComplete(Entity<FrontlineResourceNodeComponent> node,
        ref FrontlineResourceExtractionDoAfterEvent args)
    {
        if (args.Cancelled || args.Used is not { } tool || node.Comp.RemainingYield <= 0 ||
            !_tags.HasTag(tool, PickaxeTag) || !_interaction.InRangeUnobstructed(args.User, node.Owner))
            return;

        var amount = Math.Min(node.Comp.HarvestAmount, node.Comp.RemainingYield);
        node.Comp.RemainingYield -= amount;
        _stack.SpawnAtPosition(amount, node.Comp.Output, Transform(node).Coordinates);

        if (node.Comp.RemainingYield == 0)
            Del(node);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<FrontlineResourceFieldComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var field, out var transform))
        {
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
        var prototype = field.Comp.BonusNodePrototype is { } bonus && _random.Prob(field.Comp.BonusNodeChance)
            ? bonus
            : field.Comp.PrimaryNodePrototype;
        var node = Spawn(prototype, coordinates);
        var nodeComp = Comp<FrontlineResourceNodeComponent>(node);
        nodeComp.Field = field;
        nodeComp.SpawnPoint = spawnPoint;
        field.Comp.ActiveNodes.Add(node);
        field.Comp.RemainingReserveNodes--;
    }
}