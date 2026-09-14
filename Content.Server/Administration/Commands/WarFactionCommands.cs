using Content.Server.War;
using Content.Shared.Administration;
using Content.Shared.War;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Round)]
public sealed partial class GetFactionCommand : IConsoleCommand
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private WarFactionSystem _factions = default!;

    public string Command => "getfaction";
    public string Description => "Shows a connected player's faction in the current war.";
    public string Help => "getfaction <player>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !_players.TryGetSessionByUsername(args[0], out var player))
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_factions.TryGetFaction(player.UserId, out var faction) ? faction.Id : "unselected");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => args.Length == 1
        ? CompletionResult.FromOptions(_players.Sessions.Select(player => player.Name))
        : CompletionResult.Empty;
}

[AdminCommand(AdminFlags.Round)]
public sealed partial class SetFactionCommand : IConsoleCommand
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private WarFactionSystem _factions = default!;

    public string Command => "setfaction";
    public string Description => "Overrides a connected player's faction for the current war.";
    public string Help => "setfaction <player> <faction>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !_players.TryGetSessionByUsername(args[0], out var player))
        {
            shell.WriteError(Help);
            return;
        }

        var faction = new FactionId(args[1]);
        if (!_prototypes.HasIndex<FrontlineFactionPrototype>(faction.Id) || !_factions.SetFaction(player.UserId, faction))
        {
            shell.WriteError("Unknown faction or no active war.");
            return;
        }

        shell.WriteLine($"{player.Name}: {faction.Id}");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => args.Length switch
    {
        1 => CompletionResult.FromOptions(_players.Sessions.Select(player => player.Name)),
        2 => CompletionResult.FromOptions(_prototypes.EnumeratePrototypes<FrontlineFactionPrototype>().Select(faction => faction.ID)),
        _ => CompletionResult.Empty,
    };
}

[AdminCommand(AdminFlags.Round)]
public sealed partial class ClearFactionCommand : IConsoleCommand
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private WarFactionSystem _factions = default!;

    public string Command => "clearfaction";
    public string Description => "Clears a connected player's faction for the current war.";
    public string Help => "clearfaction <player>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !_players.TryGetSessionByUsername(args[0], out var player))
        {
            shell.WriteError(Help);
            return;
        }

        _factions.ClearFaction(player.UserId);
        shell.WriteLine($"{player.Name}: faction cleared");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => args.Length == 1
        ? CompletionResult.FromOptions(_players.Sessions.Select(player => player.Name))
        : CompletionResult.Empty;
}
