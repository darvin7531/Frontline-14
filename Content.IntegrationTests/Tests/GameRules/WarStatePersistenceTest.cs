using System.IO;
using System.Text.Json;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.War;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.GameRules;

[TestFixture]
public sealed class WarStatePersistenceTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
    };

    [Test]
    public async Task TechnicalRoundRestartPreservesWar()
    {
        var server = Pair.Server;
        var resources = server.ResolveDependency<IResourceManager>();
        var ticker = server.System<GameTicker>();
        var war = server.System<WarStateSystem>();

        await server.WaitAssertion(() =>
        {
            resources.UserData.Delete(WarStateSystem.SavePath);
            var expected = war.StartNewWar();

            ticker.RestartRound();

            Assert.That(war.State, Is.EqualTo(expected));
            using var stream = resources.UserData.Open(WarStateSystem.SavePath, FileMode.Open);
            Assert.That(JsonSerializer.Deserialize<WarState>(stream), Is.EqualTo(expected));
        });
    }
}
