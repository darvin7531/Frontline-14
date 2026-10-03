using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Players;
using Content.Shared.War;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

public sealed partial class WarPlayerLifecycleSystem : EntitySystem
{
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private PersistentWarRuleSystem _persistentWar = default!;
    [Dependency] private WarStateSystem _war = default!;
    [Dependency] private WarFactionSystem _factions = default!;
    [Dependency] private TerritorySystem _territories = default!;
    [Dependency] private FactionSpawnSystem _factionSpawns = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private static readonly ProtoId<FrontlineSupplyProductPrototype> SoldierSupplies = "SoldierSupplies";

    private readonly HashSet<NetUserId> _respawning = new();
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

    public RespawnBaseOption[] GetRespawnChoices(NetUserId account)
    {
        var choices = new List<RespawnBaseOption>();
        if (!_persistentWar.IsPersistentWarActive() ||
            _war.State is not { Status: WarStatus.Active } ||
            !_factions.TryGetFaction(account, out var faction))
            return choices.ToArray();

        foreach (var territory in _territories.GetTerritories(_ticker.DefaultMap))
        {
            if (string.IsNullOrWhiteSpace(territory.Id))
                continue;
            ProtoId<FrontlineTerritoryPrototype> id = territory.Id;
            if (!_prototypes.TryIndex(id, out var prototype) ||
                !_factionSpawns.TryGetRespawnBase(faction, territory, _ticker.DefaultMap,
                    out var core, out _, out _))
                continue;

            choices.Add(new RespawnBaseOption(territory.Id, prototype.Name,
                Comp<FrontlineStockpileComponent>(core).Counts.GetValueOrDefault(SoldierSupplies)));
        }
        return choices.ToArray();
    }

    // Trusted server callers may retain automatic funded-base selection.
    public bool RequestRespawn(NetUserId account) => RequestRespawn(account, null);

    public bool RequestRespawn(NetUserId account, TerritoryId territory) =>
        RequestRespawn(account, (TerritoryId?) territory);

    private bool RequestRespawn(NetUserId account, TerritoryId? selected)
    {
        if (!_respawning.Add(account))
            return false;
        try
        {
            if (!_persistentWar.IsPersistentWarActive() ||
                _war.State is not { Status: WarStatus.Active } ||
                !_factions.TryGetFaction(account, out var faction) ||
                !_waiting.TryGetValue(account, out var body) ||
                !_players.TryGetSessionById(account, out var session) ||
                !_mind.TryGetMind(account, out var mindId, out var mind) ||
                mindId is not { } mindEntity || mind.UserId != account ||
                mind.OwnedEntity != body || mind.CurrentEntity != body || session.AttachedEntity != body ||
                TerminatingOrDeleted(body) || EntityManager.IsQueuedForDeletion(body) ||
                !TryComp<MobStateComponent>(body, out var state) || state.CurrentState != MobState.Dead)
                return false;

            TerritoryId territory;
            if (selected is { } requested)
            {
                if (string.IsNullOrWhiteSpace(requested.Id))
                    return false;
                ProtoId<FrontlineTerritoryPrototype> id = requested.Id;
                if (!_prototypes.HasIndex(id))
                    return false;
                territory = requested;
            }
            else if (!HasSuppliedSpawn(faction, out territory))
                return false;

            if (!_persistentWar.TryRespawnPlayer(session, faction, territory, body, mindEntity,
                    () => _waiting.TryGetValue(account, out var recorded) && recorded == body))
                return false;

            // The replacement and charge are committed. Corpse cleanup cannot refund them.
            _waiting.Remove(account);
            try
            {
                CloseChoice(account);
            }
            finally
            {
                if (!TerminatingOrDeleted(body))
                    Del(body);
            }
            return true;
        }
        finally
        {
            _respawning.Remove(account);
        }
    }

    private bool HasSuppliedSpawn(FactionId faction, out TerritoryId territory)
    {
        territory = default;
        foreach (var candidate in _territories.GetTerritories(_ticker.DefaultMap))
        {
            if (!_factionSpawns.TryGetRespawnBase(faction, candidate, _ticker.DefaultMap,
                    out var core, out _, out _) ||
                Comp<FrontlineStockpileComponent>(core).Counts.GetValueOrDefault(SoldierSupplies) <= 0)
                continue;
            territory = candidate;
            return true;
        }
        return false;
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
