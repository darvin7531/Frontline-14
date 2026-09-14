using System;
using System.IO;
using System.Text.Json;
using Content.Server.GameTicking;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

namespace Content.Server.War;

public enum WarStatus : byte
{
    Active,
    Ended,
}

public sealed record WarState(int WarId, WarStatus Status, DateTimeOffset StartedAt);

public sealed class WarStateSystem : EntitySystem
{
    public static readonly ResPath SavePath = new("/persistent-war-state.json");

    [Dependency] private IResourceManager _resources = default!;

    public WarState? State { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        Load();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => Load());
    }

    public WarState EnsureWar()
    {
        return State ?? StartNewWar();
    }

    public WarState StartNewWar()
    {
        var state = new WarState((State?.WarId ?? 0) + 1, WarStatus.Active, DateTimeOffset.UtcNow);
        Save(state);
        return state;
    }

    public void EndWar()
    {
        if (State is not { } state || state.Status == WarStatus.Ended)
            return;

        Save(state with { Status = WarStatus.Ended });
    }

    private void Load()
    {
        if (!_resources.UserData.Exists(SavePath))
        {
            State = null;
            return;
        }

        using var stream = _resources.UserData.Open(SavePath, FileMode.Open);
        State = JsonSerializer.Deserialize<WarState>(stream);
    }

    private void Save(WarState state)
    {
        State = state;
        using var stream = _resources.UserData.OpenWrite(SavePath);
        JsonSerializer.Serialize(stream, state);
    }
}
