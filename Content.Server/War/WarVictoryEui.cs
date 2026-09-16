using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Shared.Eui;
using Content.Shared.War;

namespace Content.Server.War;

public sealed class WarVictoryEui(int warId, FactionId winner, int territoriesHeld, TimeSpan duration) : BaseEui
{
    public override EuiStateBase GetNewState() => new WarVictoryEuiState(warId, winner, territoriesHeld, duration);

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is ReturnToWarLobbyMessage)
        {
            IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<GameTicker>().Respawn(Player);
            Close();
            return;
        }

        base.HandleMessage(msg);
    }
}
