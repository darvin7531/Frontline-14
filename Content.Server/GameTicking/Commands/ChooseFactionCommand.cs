using Content.Server.War;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.GameTicking.Commands;

[AnyCommand]
public sealed partial class ChooseFactionCommand : IConsoleCommand
{
    [Dependency] private WarFactionSystem _factions = default!;

    public string Command => "choosefaction";
    public string Description => "Opens the current war faction selector.";
    public string Help => "choosefaction";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0 && shell.Player != null)
            _factions.OpenSelector(shell.Player);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => CompletionResult.Empty;
}
