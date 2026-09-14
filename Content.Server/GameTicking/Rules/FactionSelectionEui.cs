using Content.Server.EUI;
using Content.Server.War;
using Content.Shared.Eui;
using Content.Shared.War;
using Robust.Shared.Prototypes;

namespace Content.Server.GameTicking.Rules;

public sealed class FactionSelectionEui : BaseEui
{
    private readonly WarFactionSystem _factions;
    private readonly IPrototypeManager _prototypes;

    public FactionSelectionEui()
    {
        _factions = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<WarFactionSystem>();
        _prototypes = IoCManager.Resolve<IPrototypeManager>();
    }

    public override FactionSelectionEuiState GetNewState()
    {
        var factions = _prototypes.EnumeratePrototypes<FrontlineFactionPrototype>()
            .Select(faction => new FactionSelectionOption(new FactionId(faction.ID), Loc.GetString(faction.Name), Loc.GetString(faction.Description), faction.Color))
            .ToArray();
        return new FactionSelectionEuiState(factions);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);
        if (msg is ChooseFactionMessage selection && _factions.TrySelectFaction(Player.UserId, selection.Faction))
            Close();
    }
}
