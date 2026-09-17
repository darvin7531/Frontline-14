using Content.Shared.War;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.War;

public sealed partial class FrontlineResourceFieldSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineResourceNodeComponent, EntityTerminatingEvent>(OnNodeTerminating);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<FrontlineResourceFieldComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var field, out var transform))
        {
            if (!field.FieldInitialized)
            {
                field.RemainingReserveNodes = field.MaxReserveNodes;
                field.NextReplenishment = _timing.CurTime + field.ReplenishmentDelay;
                field.FieldInitialized = true;
            }
            else if (field.ReplenishmentDelay > TimeSpan.Zero &&
                     field.RemainingReserveNodes < field.MaxReserveNodes &&
                     _timing.CurTime >= field.NextReplenishment)
            {
                var intervals = 1 + (int) ((_timing.CurTime - field.NextReplenishment) / field.ReplenishmentDelay);
                field.RemainingReserveNodes = Math.Min(field.MaxReserveNodes,
                    field.RemainingReserveNodes + intervals);
                field.NextReplenishment += intervals * field.ReplenishmentDelay;
            }

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
        }
    }

    private void OnNodeTerminating(Entity<FrontlineResourceNodeComponent> node, ref EntityTerminatingEvent args)
    {
        if (!TryComp<FrontlineResourceFieldComponent>(node.Comp.Field, out var field) ||
            !field.ActiveNodes.Remove(node) ||
            field.RemainingReserveNodes == 0 ||
            field.ActiveNodes.Count >= field.MaxActiveNodes)
            return;

        var coordinates = Transform(node).Coordinates;
        if (TerminatingOrDeleted(coordinates.EntityId))
            return;

        SpawnNode((node.Comp.Field, field), node.Comp.SpawnPoint, coordinates);
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
        nodeComp.Field = field;
        nodeComp.SpawnPoint = spawnPoint;
        field.Comp.ActiveNodes.Add(node);
        field.Comp.RemainingReserveNodes--;
    }
}