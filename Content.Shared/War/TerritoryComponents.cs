using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Shared.War;

[RegisterComponent]
public sealed partial class TerritoryComponent : Component
{
    [DataField(required: true)]
    public TerritoryId Territory;

    [DataField(required: true)]
    public Vector2 BoundsMin;

    [DataField(required: true)]
    public Vector2 BoundsMax;

    public void Configure(TerritoryId territory, Vector2 boundsMin, Vector2 boundsMax)
    {
        Territory = territory;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
    }

    public bool Contains(Vector2 position) =>
        position.X >= BoundsMin.X && position.X <= BoundsMax.X &&
        position.Y >= BoundsMin.Y && position.Y <= BoundsMax.Y;
}

[RegisterComponent]
public sealed partial class TownHallComponent : Component
{
    [DataField(required: true)]
    public TerritoryId Territory;

    [DataField(required: true)]
    public FactionId Faction;

    public void Configure(TerritoryId territory, FactionId faction)
    {
        Territory = territory;
        Faction = faction;
    }
}

[RegisterComponent]
public sealed partial class FactionSpawnPointComponent : Component
{
    [DataField(required: true)]
    public TerritoryId Territory;
}
