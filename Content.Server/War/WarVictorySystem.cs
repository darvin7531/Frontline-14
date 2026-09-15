using Content.Shared.War;
using Robust.Shared.GameObjects;

namespace Content.Server.War;

public sealed partial class WarVictorySystem : EntitySystem
{
    private const int TerritoryCount = 5;
    private const int RequiredOwnedTerritories = 4;

    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private WarStateSystem _war = default!;

    public override void Update(float frameTime)
    {
        CheckForVictory();
    }

    public bool CheckForVictory()
    {
        if (_war.State is not { Status: WarStatus.Active })
            return false;

        var territoryIds = new HashSet<TerritoryId>();
        var territories = EntityQueryEnumerator<TerritoryComponent>();
        while (territories.MoveNext(out var uid, out var territory))
        {
            if (!TerminatingOrDeleted(uid))
                territoryIds.Add(new TerritoryId(territory.TerritoryId));
        }

        if (territoryIds.Count != TerritoryCount)
            return false;

        var owned = new Dictionary<FactionId, int>();
        foreach (var territory in territoryIds)
        {
            if (!_territories.TryGetOwner(territory, out var faction))
                continue;

            owned[faction] = owned.GetValueOrDefault(faction) + 1;
            if (owned[faction] >= RequiredOwnedTerritories)
            {
                _war.EndWar(faction);
                return true;
            }
        }

        return false;
    }
}