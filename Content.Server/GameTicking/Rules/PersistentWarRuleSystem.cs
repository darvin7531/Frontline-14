using Content.Server.GameTicking.Rules.Components;
using Content.Server.Ghost;
using Content.Server.Light.EntitySystems;
using Content.Server.Maps;
using Content.Server.Station.Systems;
using Content.Server.War;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Mind;
using Content.Shared.Preferences;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server.GameTicking.Rules;

public sealed partial class PersistentWarRuleSystem : GameRuleSystem<PersistentWarRuleComponent>
{

    [Dependency] private FactionSpawnSystem _factionSpawns = default!;
    [Dependency] private WarFactionSystem _factions = default!;
    [Dependency] private WarStateSystem _war = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private StationSpawningSystem _stationSpawning = default!;
    [Dependency] private LightCycleSystem _lightCycle = default!;
    [Dependency] private PersistentWarMapValidatorSystem _mapValidator = default!;
    [Dependency] private SharedMapSystem _map = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LoadingMapsEvent>(OnLoadingMaps);
        SubscribeLocalEvent<RulePlayerSpawningEvent>(OnRulePlayerSpawning);
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnPlayerBeforeSpawn);
        SubscribeLocalEvent<GhostAttemptHandleEvent>(OnGhostAttempt);
    }

    private void OnLoadingMaps(LoadingMapsEvent args)
    {
        if (GameTicker.CurrentPreset?.ID != "PersistentWar" ||
            GameTicker.CurrentPreset.MapPool is not { } poolId ||
            !ProtoMan.TryIndex<GameMapPoolPrototype>(poolId, out var pool))
            return;

        foreach (var mapId in pool.Maps)
        {
            args.Maps.Clear();
            args.Maps.Add(ProtoMan.Index<GameMapPrototype>(mapId));
            return;
        }
    }

    private static TimeSpan GetCycleOffset(DateTimeOffset startedAt, TimeSpan duration)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return elapsed <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(elapsed.Ticks % duration.Ticks);
    }

    private void OnRulePlayerSpawning(RulePlayerSpawningEvent args)
    {
        if (!IsPersistentWarActive())
            return;

        var map = _map.GetMap(GameTicker.DefaultMap);
        var war = _war.EnsureWar();
        _mapValidator.Validate(map);
        var cycle = Comp<LightCycleComponent>(map);
        _lightCycle.SetOffset((map, cycle), GetCycleOffset(war.StartedAt, cycle.Duration));

        for (var i = args.PlayerPool.Count - 1; i >= 0; i--)
        {
            var player = args.PlayerPool[i];
            args.PlayerPool.RemoveAt(i);

            if (!_factions.TryGetFaction(player.UserId, out var faction) ||
                !TrySpawnPlayer(player, args.Profiles[player.UserId], faction))
                continue;

            GameTicker.PlayerJoinGame(player);
        }
    }

    private void OnPlayerBeforeSpawn(PlayerBeforeSpawnEvent args)
    {
        if (!IsPersistentWarActive())
            return;

        if (_factions.TryGetFaction(args.Player.UserId, out var faction))
            TrySpawnPlayer(args.Player, args.Profile, faction);

        args.Handled = true;
    }

    private void OnGhostAttempt(GhostAttemptHandleEvent args)
    {
        if (!IsPersistentWarActive())
            return;

        args.Handled = true;
        args.Result = false;
    }

    private bool TrySpawnPlayer(ICommonSession player, HumanoidCharacterProfile profile, FactionId faction)
    {
        var spawns = _factionSpawns.GetAvailableSpawns(faction);
        if (spawns.Count == 0)
            return false;

        var mind = _mind.GetOrCreateMind(player.UserId);
        if (mind.Comp.OwnedEntity is { } current &&
            !TerminatingOrDeleted(current))
            return false;

        var mob = _stationSpawning.SpawnPlayerMob(RobustRandom.Pick(spawns), null, profile, null);
        _mind.TransferTo(mind, mob, ghostCheckOverride: true);
        return true;
    }

    public bool IsPersistentWarActive()
    {
        var rules = EntityQueryEnumerator<PersistentWarRuleComponent, GameRuleComponent>();
        while (rules.MoveNext(out var uid, out _, out var gameRule))
        {
            if (GameTicker.IsGameRuleActive(uid, gameRule))
                return true;
        }

        return false;
    }
}
