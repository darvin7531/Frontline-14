using Content.Server.GameTicking.Rules.Components;
using Content.Server.Ghost;
using Content.Server.Light.EntitySystems;
using Content.Server.Maps;
using Content.Server.Mind;
using Content.Server.Station.Systems;
using Content.Server.War;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Players;
using Content.Shared.Preferences;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.GameTicking.Rules;

public sealed partial class PersistentWarRuleSystem : GameRuleSystem<PersistentWarRuleComponent>
{

    private static readonly ProtoId<FrontlineSupplyProductPrototype> SoldierSupplies = "SoldierSupplies";

    [Dependency] private FactionSpawnSystem _factionSpawns = default!;
    [Dependency] private WarFactionSystem _factions = default!;
    [Dependency] private WarStateSystem _war = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private FrontlineStockpileSystem _stockpiles = default!;
    [Dependency] private TerritorySystem _territories = default!;
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
        var spawns = _factionSpawns.GetAvailableSpawns(faction, GameTicker.DefaultMap);
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

    private bool Usable(EntityUid uid) =>
        !TerminatingOrDeleted(uid) && !EntityManager.IsQueuedForDeletion(uid);

    public bool TryRespawnPlayer(ICommonSession player, FactionId faction, TerritoryId territory,
        EntityUid oldBody, EntityUid mindId, Func<bool> isWaitingBody)
    {
        if (_war.State is not { Status: WarStatus.Active } war ||
            !_mind.TryGetMind(player.UserId, out var currentMind, out var mind) || currentMind != mindId ||
            !_factionSpawns.TryGetRespawnBase(faction, territory, GameTicker.DefaultMap,
                out var core, out var point, out var coordinates))
            return false;

        var mapId = GameTicker.DefaultMap;
        var stockpile = Comp<FrontlineStockpileComponent>(core);
        var supplies = stockpile.Counts.GetValueOrDefault(SoldierSupplies);
        var spending = false;
        bool ContextValid() =>
            isWaitingBody() && IsPersistentWarActive() && _war.State == war && GameTicker.DefaultMap == mapId &&
            _factions.TryGetFaction(player.UserId, out var selected) && selected == faction &&
            Usable(oldBody) && Usable(mindId) && mind.UserId == player.UserId &&
            _mind.TryGetMind(player.UserId, out var current, out var component) &&
            current == mindId && component == mind &&
            TryComp<MobStateComponent>(oldBody, out var dead) && dead.CurrentState == MobState.Dead &&
            _factionSpawns.TryGetRespawnBase(faction, territory, mapId,
                out var currentCore, out var currentPoint, out var currentCoordinates) &&
            currentCore == core && currentPoint == point && currentCoordinates == coordinates &&
            TryComp<FrontlineStockpileComponent>(core, out var currentStockpile) && currentStockpile == stockpile &&
            stockpile.Counts.GetValueOrDefault(SoldierSupplies) == supplies - (spending ? 1 : 0);

        bool StillInOldBody() =>
            ContextValid() && mind.OwnedEntity == oldBody && mind.CurrentEntity == oldBody &&
            player.AttachedEntity == oldBody &&
            TryComp<ActorComponent>(oldBody, out var actor) && actor.PlayerSession == player;

        bool ReplacementValid(EntityUid mob) =>
            Usable(mob) && TryComp<MobStateComponent>(mob, out var state) && state.CurrentState == MobState.Alive &&
            TryComp(mob, out TransformComponent? xform) && xform.MapID == mapId &&
            xform.Coordinates.EntityId == coordinates.EntityId &&
            _territories.Contains(territory, xform.Coordinates);

        if (!StillInOldBody())
            return false;

        return _stockpiles.TrySpendSoldierSupply(core, () =>
        {
            spending = true;
            EntityUid? replacement = null;
            var committed = false;
            try
            {
                if (!StillInOldBody())
                    return false;
                var profile = GameTicker.GetPlayerProfile(player);
                if (!ProtoMan.TryIndex<SpeciesPrototype>(profile.Species, out var species))
                    return false;
                // Capture the body before the native profile/gear callbacks so a throw can clean it up.
                replacement = Spawn(species.Prototype, coordinates);
                if (!StillInOldBody() || !ReplacementValid(replacement.Value))
                    return false;
                _stationSpawning.SpawnPlayerMob(coordinates, null, profile, null, entity: replacement);
                if (!StillInOldBody() || !ReplacementValid(replacement.Value))
                    return false;

                _mind.TransferTo(mindId, replacement, ghostCheckOverride: true, createGhost: false, mind: mind);
                committed = ContextValid() && ReplacementValid(replacement.Value) &&
                    mind.OwnedEntity == replacement && mind.CurrentEntity == replacement &&
                    player.AttachedEntity == replacement &&
                    _mind.TryGetMind(replacement.Value, out var attachedMind, out var attachedComponent) &&
                    attachedMind == mindId && attachedComponent == mind &&
                    TryComp<ActorComponent>(replacement, out var actor) && actor.PlayerSession == player;
                return committed;
            }
            finally
            {
                if (!committed && replacement is { } mob)
                {
                    try
                    {
                        // Keep the corpse (or a body revived by a callback); never delete it on rejection.
                        if (Usable(oldBody) && Usable(mindId) && mind.UserId == player.UserId &&
                            _mind.TryGetMind(player.UserId, out var current, out var component) &&
                            current == mindId && component == mind &&
                            (mind.OwnedEntity == mob || mind.CurrentEntity == mob || mind.OwnedEntity == null))
                            _mind.TransferTo(mindId, oldBody, ghostCheckOverride: true, createGhost: false, mind: mind);
                    }
                    finally
                    {
                        if (!TerminatingOrDeleted(mob))
                            QueueDel(mob);
                    }
                }
            }
        });
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
