using Content.Client.Eui;
using Content.Shared.Eui;
using Content.Shared.War;
using JetBrains.Annotations;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client.War;

[UsedImplicitly]
public sealed class RespawnChoiceEui : BaseEui
{
    private readonly DefaultWindow _window = new();

    public RespawnChoiceEui()
    {
        _window.Title = Loc.GetString("frontline-respawn-choice-title");
        var contents = new BoxContainer { Orientation = LayoutOrientation.Vertical };
        var wait = new Button { Text = Loc.GetString("frontline-respawn-choice-wait") };
        wait.OnPressed += _ => SendMessage(new CloseEuiMessage());
        contents.AddChild(wait);
        var respawn = new Button { Text = Loc.GetString("frontline-respawn-choice-respawn") };
        respawn.OnPressed += _ => SendMessage(new RespawnNowMessage());
        contents.AddChild(respawn);
        _window.ContentsContainer.AddChild(contents);
    }

    public override void Opened() => _window.OpenCentered();

    public override void Closed() => _window.Close();

    public override void HandleState(EuiStateBase state)
    {
    }
}
