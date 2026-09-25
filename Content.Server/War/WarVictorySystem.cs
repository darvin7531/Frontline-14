using System.Linq;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Shared.War;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class WarVictorySystem : EntitySystem
{
    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private WarStateSystem _war = default!;
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedMapSystem _maps = default!;

    public override void Update(float frameTime)
    {
        CheckForVictory();
    }

    public bool CheckForVictory(MapId? mapId = null)
    {
        if (mapId == null && !_maps.MapExists(_ticker.DefaultMap))
            return false;

        var warMap = mapId ?? _ticker.DefaultMap;
        if (_war.State is not { Status: WarStatus.Active } ||
            !_prototypes.TryIndex<FrontlineWarPrototype>(FrontlineWarPrototype.MainWar, out var definition) ||
            definition.RequiredVictoryPoints <= 0 || definition.Territories.Count == 0)
            return false;

        var configured = definition.Territories.Select(id => new TerritoryId(id.Id)).ToHashSet();
        if (configured.Count != definition.Territories.Count || !_territories.GetTerritories(warMap).SetEquals(configured))
            return false;

        var points = new Dictionary<FactionId, long>();
        var held = new Dictionary<FactionId, int>();
        foreach (var territory in configured)
        {
            if (!_territories.TryGetOwner(territory, out var faction, warMap))
                continue;

            if (!_prototypes.TryIndex<FrontlineTerritoryPrototype>(territory.Id, out var prototype) ||
                prototype.VictoryPoints <= 0)
                return false;

            held[faction] = held.GetValueOrDefault(faction) + 1;
            points[faction] = points.GetValueOrDefault(faction) + prototype.VictoryPoints;
        }

        foreach (var (faction, score) in points)
        {
            if (score < definition.RequiredVictoryPoints)
                continue;

            _war.EndWar(faction);
            var duration = DateTimeOffset.UtcNow - _war.State!.StartedAt;
            foreach (var player in _players.Sessions)
            {
                _eui.OpenEui(new WarVictoryEui(_war.State.WarId, faction, held[faction], definition.Territories.Count, duration), player);
            }

            return true;
        }

        return false;
    }
}