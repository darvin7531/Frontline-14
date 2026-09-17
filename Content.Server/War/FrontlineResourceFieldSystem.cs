using Content.Shared.War;

namespace Content.Server.War;

public sealed class FrontlineResourceFieldSystem : EntitySystem
{
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

                var node = Spawn(field.PrimaryNodePrototype, slotTransform.Coordinates);
                Comp<FrontlineResourceNodeComponent>(node).Field = uid;
                field.ActiveNodes.Add(node);
                field.RemainingReserveNodes--;
            }

            field.Initialized = true;
        }
    }
}