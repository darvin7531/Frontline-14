using Content.Server.EUI;
using Content.Shared.Eui;
using Content.Shared.War;

namespace Content.Server.War;

public sealed class RespawnChoiceEui : BaseEui
{
    private readonly WarPlayerLifecycleSystem _lifecycle;

    public RespawnChoiceEui()
    {
        _lifecycle = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<WarPlayerLifecycleSystem>();
    }

    public override void Opened() => StateDirty();

    public override EuiStateBase GetNewState() => new RespawnChoiceEuiState(_lifecycle.GetRespawnChoices(Player.UserId));

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
                StateDirty();
            return;
        }

        base.HandleMessage(msg);
    }
}
