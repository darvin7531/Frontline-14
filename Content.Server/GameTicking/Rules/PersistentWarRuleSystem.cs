using Content.Server.GameTicking.Rules.Components;
using Content.Server.Ghost;
using Content.Server.War;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mind;
using Content.Shared.Preferences;
using Content.Shared.War;
using Robust.Shared.Player;

namespace Content.Server.GameTicking.Rules;

public sealed partial class PersistentWarRuleSystem : GameRuleSystem<PersistentWarRuleComponent>
{

    [Dependency] private FactionSpawnSystem _factionSpawns = default!;
    [Dependency] private WarFactionSystem _factions = default!;
    [Dependency] private WarStateSystem _war = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RulePlayerSpawningEvent>(OnRulePlayerSpawning);
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnPlayerBeforeSpawn);
        SubscribeLocalEvent<GhostAttemptHandleEvent>(OnGhostAttempt);
    }

    protected override void Started(EntityUid uid, PersistentWarRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        _war.EnsureWar();
    }

    private void OnRulePlayerSpawning(RulePlayerSpawningEvent args)
    {
        if (!IsPersistentWarActive())
            return;

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
        if (!IsPersistentWarActive() ||
            !_factions.TryGetFaction(args.Player.UserId, out var faction) ||
            !TrySpawnPlayer(args.Player, args.Profile, faction))
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

    private bool TrySpawnPlayer(ICommonSession player, HumanoidCharacterProfile profile, FactionId faction)
    {
        var spawns = _factionSpawns.GetAvailableSpawns(faction);
        if (spawns.Count == 0)
            return false;

        // Spawn providers will own materializing the faction character when map entities exist.
        return false;
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
