using Content.Shared.War;
using Robust.Shared.Map;

namespace Content.Server.War;

public sealed class FrontlineResourceFieldSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<FrontlineResourceNodeComponent, EntityTerminatingEvent>(OnNodeTerminating);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<FrontlineResourceFieldComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var field, out var transform))
        {
            if (field.Initialized)
                continue;

            field.RemainingReserveNodes = field.MaxReserveNodes;
            var slots = EntityQueryEnumerator<FrontlineResourceSpawnPointComponent, TransformComponent>();
            while (slots.MoveNext(out _, out var slot, out var slotTransform))
            {
                if (field.ActiveNodes.Count >= field.MaxActiveNodes || field.RemainingReserveNodes == 0)
                    break;

                if (slot.FieldId != field.FieldId || slotTransform.MapID != transform.MapID)
                    continue;

                SpawnNode((uid, field), slotTransform.Coordinates);
            }

            field.Initialized = true;
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

        SpawnNode((node.Comp.Field, field), coordinates);
    }

    private void SpawnNode(Entity<FrontlineResourceFieldComponent> field, EntityCoordinates coordinates)
    {
        var node = Spawn(field.Comp.PrimaryNodePrototype, coordinates);
        Comp<FrontlineResourceNodeComponent>(node).Field = field;
        field.Comp.ActiveNodes.Add(node);
        field.Comp.RemainingReserveNodes--;
    }
}