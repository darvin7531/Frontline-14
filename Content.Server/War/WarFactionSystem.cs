using System.Text.Json;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Shared.GameTicking;
using Content.Shared.War;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.War;

public sealed record WarFactionMembership(Guid AccountId, int WarId, FactionId FactionId, DateTimeOffset JoinedAt);

public sealed partial class WarFactionSystem : EntitySystem
{
    public static readonly ResPath SavePath = new("/persistent-war-factions.json");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private WarStateSystem _war = default!;
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private GameTicker _ticker = default!;

    private List<WarFactionMembership> _memberships = [];

    public override void Initialize()
    {
        base.Initialize();
        Load();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => Load());
    }

    public bool TryGetFaction(NetUserId account, out FactionId faction)
    {
        var war = _war.State;
        var membership = war == null
            ? null
            : _memberships.FirstOrDefault(member => member.AccountId == account.UserId && member.WarId == war.WarId);

        if (membership == null)
        {
            faction = default;
            return false;
        }

        faction = membership.FactionId;
        return true;
    }

    public bool TrySelectFaction(NetUserId account, FactionId faction)
    {
        if (_war.State is not { Status: WarStatus.Active } war ||
            !_prototypes.HasIndex<FrontlineFactionPrototype>(faction.Id) ||
            TryGetFaction(account, out _))
            return false;

        _memberships.Add(new WarFactionMembership(account.UserId, war.WarId, faction, DateTimeOffset.UtcNow));
        Save();
        _ticker.SendStatusToAll();
        return true;
    }

    public void OpenSelector(ICommonSession player)
    {
        if (_war.State is { Status: WarStatus.Active } && !TryGetFaction(player.UserId, out _))
            _eui.OpenEui(new FactionSelectionEui(), player);
    }

    public bool SetFaction(NetUserId account, FactionId faction)
    {
        if (_war.State is not { } war || !_prototypes.HasIndex<FrontlineFactionPrototype>(faction.Id))
            return false;

        _memberships.RemoveAll(member => member.AccountId == account.UserId && member.WarId == war.WarId);
        _memberships.Add(new WarFactionMembership(account.UserId, war.WarId, faction, DateTimeOffset.UtcNow));
        Save();
        _ticker.SendStatusToAll();
        return true;
    }

    public void ClearFaction(NetUserId account)
    {
        if (_war.State is not { } war)
            return;

        if (_memberships.RemoveAll(member => member.AccountId == account.UserId && member.WarId == war.WarId) > 0)
        {
            Save();
            _ticker.SendStatusToAll();
        }
    }

    private void Load()
    {
        if (!_resources.UserData.Exists(SavePath))
        {
            _memberships = [];
            return;
        }

        using var stream = _resources.UserData.Open(SavePath, FileMode.Open);
        _memberships = JsonSerializer.Deserialize<List<WarFactionMembership>>(stream) ?? [];
    }

    private void Save()
    {
        using var stream = _resources.UserData.OpenWrite(SavePath);
        JsonSerializer.Serialize(stream, _memberships);
    }
}
