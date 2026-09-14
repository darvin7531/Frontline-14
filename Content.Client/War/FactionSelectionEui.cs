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
    private readonly FactionSelectionWindow _window = new();

    public FactionSelectionEui()
    {
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void Opened() => _window.OpenCentered();

    public override void Closed() => _window.Close();

    public override void HandleState(EuiStateBase state)
    {
        if (state is FactionSelectionEuiState selection)
            _window.SetFactions(selection.Factions, faction => SendMessage(new ChooseFactionMessage(faction)));
    }
}

public sealed class FactionSelectionWindow : DefaultWindow
{
    private readonly BoxContainer _factions = new() { Orientation = LayoutOrientation.Vertical };

    public FactionSelectionWindow()
    {
        Title = Loc.GetString("frontline-faction-select-title");
        ContentsContainer.AddChild(_factions);
    }

    public void SetFactions(IEnumerable<FactionSelectionOption> factions, Action<FactionId> choose)
    {
        _factions.RemoveAllChildren();
        foreach (var faction in factions)
        {
            var button = new Button { Text = faction.Name, ModulateSelfOverride = faction.Color };
            button.OnPressed += _ => choose(faction.Id);
            _factions.AddChild(new Label { Text = faction.Description });
            _factions.AddChild(button);
        }
    }
}
