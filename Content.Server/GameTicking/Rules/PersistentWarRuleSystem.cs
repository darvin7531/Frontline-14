using Content.Server.GameTicking.Rules.Components;
using Content.Server.Ghost;
using Content.Server.Mind;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mind;
using Content.Shared.Preferences;
using Robust.Shared.Player;

namespace Content.Server.GameTicking.Rules;

public sealed partial class PersistentWarRuleSystem : GameRuleSystem<PersistentWarRuleComponent>
{
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private StationSpawningSystem _stationSpawning = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RulePlayerSpawningEvent>(OnRulePlayerSpawning);
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnPlayerBeforeSpawn);
        SubscribeLocalEvent<GhostAttemptHandleEvent>(OnGhostAttempt);
    }

    private void OnRulePlayerSpawning(RulePlayerSpawningEvent args)
    {
        if (!IsPersistentWarActive())
            return;

        for (var i = args.PlayerPool.Count - 1; i >= 0; i--)
        {
            var player = args.PlayerPool[i];
            if (!TrySpawnPlayer(player, args.Profiles[player.UserId]))
                continue;

            args.PlayerPool.RemoveAt(i);
            GameTicker.PlayerJoinGame(player);
        }
    }

    private void OnPlayerBeforeSpawn(PlayerBeforeSpawnEvent args)
    {
        if (!IsPersistentWarActive() || !TrySpawnPlayer(args.Player, args.Profile))
            return;

        args.Handled = true;
    }

    private void OnGhostAttempt(GhostAttemptHandleEvent args)
    {
        if (!IsPersistentWarActive())
            return;

        args.Handled = true;
        args.Result = false;
    }

    private bool TrySpawnPlayer(ICommonSession player, HumanoidCharacterProfile profile)
    {
        var stations = EntityQueryEnumerator<StationSpawningComponent>();
        if (!stations.MoveNext(out var station, out _))
            return false;

        var mob = _stationSpawning.SpawnPlayerCharacterOnStation(station, null, profile);
        if (mob == null)
            return false;

        if (_mind.TryGetMind(player, out _, out _))
            _mind.WipeMind(player);

        var mind = _mind.CreateMind(player.UserId, profile.Name);
        _mind.SetUserId(mind, player.UserId);
        _mind.TransferTo(mind, mob.Value);
        return true;
    }

    private bool IsPersistentWarActive()
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
