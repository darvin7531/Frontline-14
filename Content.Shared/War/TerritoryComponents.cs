using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Content.Shared.Stacks;

namespace Content.Shared.War;

public enum FrontlineResourceFieldState : byte
{
    Active,
    Depleted,
    Replenishing,
}

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

[RegisterComponent]
public sealed partial class FrontlineResourceFieldComponent : Component
{
    [DataField(required: true)]
    public string FieldId = string.Empty;

    [DataField(required: true)]
    public EntProtoId PrimaryNodePrototype;

    [DataField]
    public EntProtoId? BonusNodePrototype;

    [DataField]
    public float BonusNodeChance;

    [DataField]
    public int MaxReserveNodes;

    [DataField]
    public int MaxActiveNodes;

    [DataField]
    public TimeSpan ReplacementDelay;

    [DataField]
    public TimeSpan ReplenishmentDelay;

    public int RemainingReserveNodes;
    public readonly HashSet<EntityUid> ActiveNodes = new();
    public bool FieldInitialized;
    public FrontlineResourceFieldState State;
    public TimeSpan NextReplacement;
    public TimeSpan NextReplenishment;
}

[RegisterComponent]
public sealed partial class FrontlineResourceSpawnPointComponent : Component
{
    [DataField(required: true)]
    public string FieldId = string.Empty;
}

[RegisterComponent]
public sealed partial class FrontlineResourceNodeComponent : Component
{
    [DataField(required: true)]
    public ProtoId<StackPrototype> Output;

    [DataField]
    public int MaxYield = 1;

    [DataField]
    public int HarvestAmount = 1;

    [DataField]
    public TimeSpan ExtractionTime = TimeSpan.FromSeconds(2);

    public int RemainingYield;
    public EntityUid Field;
    public EntityUid SpawnPoint;
}
