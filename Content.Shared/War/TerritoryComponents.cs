using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Shared.War;

[RegisterComponent]
public sealed partial class TerritoryComponent : Component
{
    [DataField("territory", required: true)]
    public string TerritoryId = string.Empty;

    [DataField(required: true)]
    public Vector2 BoundsMin;

    [DataField(required: true)]
    public Vector2 BoundsMax;

    public void Configure(TerritoryId territory, Vector2 boundsMin, Vector2 boundsMax)
    {
        TerritoryId = territory.Id;
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
    [DataField("territory", required: true)]
    public string TerritoryId = string.Empty;

    [DataField("faction", required: true)]
    public string FactionId = string.Empty;

    public void Configure(TerritoryId territory, FactionId faction)
    {
        TerritoryId = territory.Id;
        FactionId = faction.Id;
    }
}

[RegisterComponent]
public sealed partial class TownHallRuinComponent : Component
{
    [DataField("territory", required: true)]
    public string TerritoryId = string.Empty;

    [DataField]
    public int RequiredSteel = 20;

    public int DepositedSteel;
}

[RegisterComponent]
public sealed partial class PropertyComponent : Component
{
    [DataField("property", required: true)]
    public string PropertyId = string.Empty;

    public Guid? OwnerAccountId;

    public void Claim(PropertyId property, Guid owner)
    {
        PropertyId = property.Id;
        OwnerAccountId = owner;
    }
}

[RegisterComponent]
public sealed partial class FactionSpawnPointComponent : Component
{
    [DataField("territory", required: true)]
    public string TerritoryId = string.Empty;
}
