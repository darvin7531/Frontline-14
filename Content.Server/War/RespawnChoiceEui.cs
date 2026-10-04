using System.Linq;
using Content.Server.EUI;
using Content.Shared.Eui;
using Content.Shared.War;

namespace Content.Server.War;

public sealed class RespawnChoiceEui : BaseEui
{
    private readonly WarPlayerLifecycleSystem _lifecycle;
    private RespawnBaseOption[] _bases = Array.Empty<RespawnBaseOption>();

    public RespawnChoiceEui()
    {
        _lifecycle = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<WarPlayerLifecycleSystem>();
    }

    public override void Opened()
    {
        RefreshState();
        StateDirty();
    }

    public override EuiStateBase GetNewState() => new RespawnChoiceEuiState(_bases);

    public void RefreshState()
    {
        var bases = _lifecycle.GetRespawnChoices(Player.UserId);
        if (_bases.SequenceEqual(bases))
            return;

        _bases = bases;
        StateDirty();
    }

    public override void Closed()
    {
        _lifecycle.ChoiceClosed(Player.UserId, this);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is RespawnNowMessage respawn)
        {
            if (string.IsNullOrWhiteSpace(respawn.TerritoryId) ||
                !_lifecycle.RequestRespawn(Player.UserId, new TerritoryId(respawn.TerritoryId)))
            {
                RefreshState();
                StateDirty();
            }
            return;
        }

        base.HandleMessage(msg);
    }
}
