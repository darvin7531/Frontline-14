using System;
using System.Globalization;
using System.Linq;
using Content.Server.GameTicking;
using Content.Server.Light.EntitySystems;
using Content.Server.War;
using Content.Shared.Administration;
using Content.Shared.Light.Components;
using Content.Shared.War;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Round)]
public sealed partial class WarStateCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "warstate";
    public string Description => "Shows the current persistent war state.";
    public string Help => "warstate";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var state = _entities.System<WarStateSystem>().State;
        shell.WriteLine(state == null
            ? "No persistent war."
            : $"War {state.WarId}: {state.Status}, started {state.StartedAt:O}, winner {state.Winner?.Id ?? "none"}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => CompletionResult.Empty;
}

[AdminCommand(AdminFlags.Round)]
public sealed partial class TerritoriesCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "territories";
    public string Description => "Lists territory ownership on the active map.";
    public string Help => "territories";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var ticker = _entities.System<GameTicker>();
        var map = _entities.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
        if (!_entities.TryGetComponent(map, out MapComponent? mapComponent))
        {
            shell.WriteError("No active map.");
            return;
        }

        var territories = _entities.System<TerritorySystem>();
        foreach (var territory in territories.GetTerritories(mapComponent.MapId).OrderBy(id => id.Id))
        {
            var owner = territories.TryGetOwner(territory, out var faction, mapComponent.MapId) ? faction.Id : "neutral";
            var name = _prototypes.TryIndex<FrontlineTerritoryPrototype>(territory.Id, out var prototype)
                ? Loc.GetString(prototype.Name)
                : territory.Id;
            shell.WriteLine($"{name} [{territory.Id}]: {territories.GetState(territory, mapComponent.MapId)} ({owner})");
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => CompletionResult.Empty;
}

[AdminCommand(AdminFlags.Round)]
public sealed partial class CaptureTerritoryCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "captureterritory";
    public string Description => "Replaces a territory objective with the selected faction's town hall.";
    public string Help => "captureterritory <territory> <faction>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || _entities.System<WarStateSystem>().State is not { Status: WarStatus.Active })
        {
            shell.WriteError(Help);
            return;
        }

        var territory = new TerritoryId(args[0]);
        var faction = new FactionId(args[1]);
        var ticker = _entities.System<GameTicker>();
        var maps = _entities.System<SharedMapSystem>();
        if (!maps.MapExists(ticker.DefaultMap))
        {
            shell.WriteError("No active map.");
            return;
        }

        if (!_entities.System<TownHallSystem>().ForceCapture(territory, faction, ticker.DefaultMap))
        {
            shell.WriteError("Unknown territory, faction, or objective.");
            return;
        }

        shell.WriteLine($"{territory.Id}: captured by {faction.Id}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var ticker = _entities.System<GameTicker>();
            var maps = _entities.System<SharedMapSystem>();
            var territories = maps.MapExists(ticker.DefaultMap)
                ? _entities.System<TerritorySystem>().GetTerritories(ticker.DefaultMap).Select(id => id.Id)
                : Enumerable.Empty<string>();
            return CompletionResult.FromOptions(territories);
        }

        return args.Length == 2
            ? CompletionResult.FromOptions(_prototypes.EnumeratePrototypes<FrontlineFactionPrototype>().Select(faction => faction.ID))
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed partial class NewWarCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "newwar";
    public string Description => "Starts a new persistent war and reloads a clean campaign map.";
    public string Help => "newwar";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var state = _entities.System<WarStateSystem>().StartNewWar();
        _entities.System<GameTicker>().RestartRound();
        shell.WriteLine($"Started war {state.WarId} at {state.StartedAt:O}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => CompletionResult.Empty;
}

[AdminCommand(AdminFlags.Round)]
public sealed partial class WarPhaseCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "warphase";
    public string Description => "Shows or temporarily sets the live day/night phase in seconds; overrides do not persist across restarts.";
    public string Help => "warphase [seconds]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }

        var ticker = _entities.System<GameTicker>();
        var map = _entities.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
        if (!_entities.TryGetComponent(map, out LightCycleComponent? cycle) || cycle.Duration <= TimeSpan.Zero)
        {
            shell.WriteError("The active map has no day/night cycle.");
            return;
        }

        if (args.Length == 1)
        {
            if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) ||
                seconds < 0 || seconds >= cycle.Duration.TotalSeconds)
            {
                shell.WriteError($"Phase must be between 0 and {cycle.Duration.TotalSeconds} seconds.");
                return;
            }

            _entities.System<LightCycleSystem>().SetPhase((map, cycle), TimeSpan.FromSeconds(seconds));
        }

        var phase = _entities.System<LightCycleSystem>().GetPhase((map, cycle));
        shell.WriteLine($"Day/night phase: {phase.TotalSeconds:F1}/{cycle.Duration.TotalSeconds:F1} seconds.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => args.Length == 1
        ? CompletionResult.FromHint("<seconds>")
        : CompletionResult.Empty;
}