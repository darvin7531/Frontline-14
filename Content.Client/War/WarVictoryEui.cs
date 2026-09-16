using Content.Client.Eui;
using Content.Shared.Eui;
using Content.Shared.War;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client.War;

[UsedImplicitly]
public sealed class WarVictoryEui : BaseEui
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly WarVictoryWindow _window = new();

    public WarVictoryEui()
    {
        IoCManager.InjectDependencies(this);
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.Return.OnPressed += _ => SendMessage(new ReturnToWarLobbyMessage());
    }

    public override void Opened() => _window.OpenCentered();
    public override void Closed() => _window.Close();

    public override void HandleState(EuiStateBase state)
    {
        if (state is not WarVictoryEuiState victory)
            return;

        _window.Title = Loc.GetString("frontline-victory-title", ("warId", victory.WarId));
        var faction = _prototypes.TryIndex<FrontlineFactionPrototype>(victory.Winner.Id, out var prototype)
            ? Loc.GetString(prototype.Name)
            : victory.Winner.Id;
        _window.SetVictory(victory, faction);
    }
}

public sealed class WarVictoryWindow : DefaultWindow
{
    public readonly Button Return = new();

    public void SetVictory(WarVictoryEuiState victory, string faction)
    {
        ContentsContainer.RemoveAllChildren();
        var contents = new BoxContainer { Orientation = LayoutOrientation.Vertical };
        contents.AddChild(new Label { Text = Loc.GetString("frontline-victory-faction", ("faction", faction)) });
        contents.AddChild(new Label { Text = Loc.GetString("frontline-victory-territories", ("owned", victory.TerritoriesHeld), ("total", 5)) });
        contents.AddChild(new Label { Text = Loc.GetString("frontline-victory-duration", ("days", victory.Duration.Days), ("hours", victory.Duration.Hours)) });
        Return.Text = Loc.GetString("frontline-victory-return-lobby");
        contents.AddChild(Return);
        ContentsContainer.AddChild(contents);
    }
}
