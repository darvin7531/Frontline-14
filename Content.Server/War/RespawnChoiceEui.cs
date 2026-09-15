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

    public override EuiStateBase GetNewState() => new RespawnChoiceEuiState();

    public override void Closed()
    {
        _lifecycle.ChoiceClosed(Player.UserId, this);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is RespawnNowMessage && _lifecycle.RequestRespawn(Player.UserId))
            return;

        base.HandleMessage(msg);
    }
}
