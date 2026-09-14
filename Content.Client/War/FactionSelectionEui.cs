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
public sealed class FactionSelectionEui : BaseEui
{
    private readonly DefaultWindow _window = new() { Title = Loc.GetString("frontline-faction-select-title") };
    private readonly BoxContainer _factions = new() { Orientation = LayoutOrientation.Vertical };

    public FactionSelectionEui()
    {
        _window.ContentsContainer.AddChild(_factions);
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not FactionSelectionEuiState selection)
            return;

        _factions.RemoveAllChildren();
        foreach (var faction in selection.Factions)
        {
            var button = new Button { Text = faction.Name, ModulateSelfOverride = faction.Color };
            button.OnPressed += _ => SendMessage(new ChooseFactionMessage(faction.Id));
            _factions.AddChild(new Label { Text = faction.Description });
            _factions.AddChild(button);
        }
    }
}
