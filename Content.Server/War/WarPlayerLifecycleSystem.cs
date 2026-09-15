using System.Linq;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.Mind;
using Content.Server.Station.Systems;
using Content.Shared.Mobs;
using Content.Shared.Players;
using Content.Shared.War;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network;

namespace Content.Server.War;

public sealed partial class WarPlayerLifecycleSystem : EntitySystem
{
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private PersistentWarRuleSystem _persistentWar = default!;
    [Dependency] private StationSystem _station = default!;

    private readonly Dictionary<NetUserId, EntityUid> _waiting = new();
    private readonly Dictionary<NetUserId, RespawnChoiceEui> _choices = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        base.Shutdown();
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (!_persistentWar.IsPersistentWarActive() || !TryComp<ActorComponent>(args.Target, out var actor))
            return;

        var account = actor.PlayerSession.UserId;
        if (args.NewMobState == MobState.Dead)
        {
            _waiting[account] = args.Target;
            OpenChoice(actor.PlayerSession);
            return;
        }

        if (_waiting.Remove(account))
            CloseChoice(account);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus == SessionStatus.InGame && _waiting.ContainsKey(args.Session.UserId))
            OpenChoice(args.Session);
    }

    public bool RequestRespawn(NetUserId account)
    {
        if (!_waiting.TryGetValue(account, out _) ||
            !_players.TryGetSessionById(account, out var session) ||
            !_mind.TryGetMind(account, out _, out var mind) ||
            mind.CurrentEntity is not { } body ||
            !TryComp<MobStateComponent>(body, out var state) ||
            state.CurrentState != MobState.Dead ||
            _station.GetStations().FirstOrNull() is not { } station)
            return false;

        _waiting.Remove(account);
        CloseChoice(account);
        QueueDel(body);
        _ticker.MakeJoinGame(session, station, silent: true);
        return true;
    }

    public void ChoiceClosed(NetUserId account, RespawnChoiceEui choice)
    {
        if (_choices.TryGetValue(account, out var current) && current == choice)
            _choices.Remove(account);
    }

    private void OpenChoice(ICommonSession player)
    {
        if (_choices.ContainsKey(player.UserId))
            return;

        var choice = new RespawnChoiceEui();
        _choices.Add(player.UserId, choice);
        _eui.OpenEui(choice, player);
    }

    private void CloseChoice(NetUserId account)
    {
        if (_choices.Remove(account, out var choice) && !choice.IsShutDown)
            choice.Close();
    }
}
