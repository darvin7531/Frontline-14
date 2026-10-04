using Content.Client.Eui;
using Content.Shared.Eui;
using Content.Shared.War;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client.War;

[UsedImplicitly]
public sealed class RespawnChoiceEui : BaseEui
{
    private readonly RespawnChoiceWindow _window = new();
    private readonly BoxContainer _bases = new() { Orientation = LayoutOrientation.Vertical };

    public RespawnChoiceEui()
    {
        _window.Title = Loc.GetString("frontline-respawn-choice-title");
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        var contents = new BoxContainer { Orientation = LayoutOrientation.Vertical };
        var wait = new Button { Text = Loc.GetString("frontline-respawn-choice-wait") };
        wait.OnPressed += _ => SendMessage(new CloseEuiMessage());
        contents.AddChild(wait);
        contents.AddChild(_bases);
        _window.SetContents(contents);
    }

    public override void Opened() => _window.OpenCentered();

    public override void Closed() => _window.Close();

    public override void HandleState(EuiStateBase state)
    {
        if (state is not RespawnChoiceEuiState choice)
            return;

        _bases.RemoveAllChildren();
        foreach (var option in choice.Bases)
        {
            var respawn = new Button
            {
                Name = $"Respawn:{option.TerritoryId}",
                Text = Loc.GetString("frontline-respawn-choice-base",
                    ("base", Loc.GetString(option.Name)), ("count", option.Count)),
                Disabled = option.Count <= 0,
            };
            respawn.OnPressed += _ => SendMessage(new RespawnNowMessage(option.TerritoryId));
            _bases.AddChild(respawn);
        }
    }
}

public sealed class RespawnChoiceWindow : DefaultWindow
{
    public void SetContents(Control contents)
    {
        ContentsContainer.AddChild(contents);
    }
}
