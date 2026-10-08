using Content.Shared.Maps;
using Content.Shared.Movement.Systems;
using Content.Shared.Vehicle.Components;
using Robust.Shared.Map;

namespace Content.Shared.Vehicle.Systems;

public sealed partial class VehicleSurfaceSpeedSystem : EntitySystem
{
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<VehicleSurfaceSpeedComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<VehicleSurfaceSpeedComponent, MoveEvent>(OnMove);
        SubscribeLocalEvent<VehicleSurfaceSpeedComponent, RefreshMovementSpeedModifiersEvent>(OnRefresh);
    }

    private void OnMapInit(Entity<VehicleSurfaceSpeedComponent> ent, ref MapInitEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnMove(Entity<VehicleSurfaceSpeedComponent> ent, ref MoveEvent args)
    {
        if (!args.OnlyRotation)
            _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnRefresh(Entity<VehicleSurfaceSpeedComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        // ponytail: classify the center tile; footprint sampling only if large vehicles need it.
        if (!_turf.TryGetTileRef(Transform(ent).Coordinates, out var tile) || tile.Value.Tile.IsEmpty ||
            ent.Comp.RoadTiles.Contains(_tiles[tile.Value.Tile.TypeId].ID))
            return;

        args.ModifySpeed(ent.Comp.OffRoadModifier);
    }
}
