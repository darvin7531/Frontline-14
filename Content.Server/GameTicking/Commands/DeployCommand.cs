using Content.Server.War;
using Content.Shared.GameTicking;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;

namespace Content.Server.GameTicking.Commands;

[AnyCommand]
public sealed partial class DeployCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "deploy";
    public string Description => "Deploys the player at their selected Frontline faction spawn.";
    public string Help => "deploy";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0 || shell.Player is not { } player)
            return;

        var ticker = _entities.System<GameTicker>();
        if (!ticker.IsPersistentWar || ticker.RunLevel != GameRunLevel.InRound || ticker.UserHasJoinedGame(player))
            return;

        var factions = _entities.System<WarFactionSystem>();
        if (!factions.TryGetFaction(player.UserId, out _))
            return;

        ticker.MakeJoinGame(player, EntityUid.Invalid, silent: true);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => CompletionResult.Empty;
}
