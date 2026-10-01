#nullable enable
using System.Linq;
using Content.IntegrationTests.Tests.Helpers;
using Content.Server.Antag;
using Content.Server.Antag.Components;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.EntityTable;
using Content.Shared.EntityTable.Conditions;
using Content.Shared.EntityTable.EntitySelectors;
using Content.Shared.GameTicking.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.GameRules;

public sealed partial class AntagGhostRoleLifecycleTest : AntagTest
{
    private static readonly EntProtoId ParadoxRule = "ParadoxCloneSpawn";

    private sealed partial class SpawnLocationListenerSystem : TestListenerSystem<AntagSelectLocationEvent>;

    [Test]
    public async Task EndedParadoxDoesNotAttemptGhostSpawningAtRoundStart()
    {
        var map = await Pair.CreateTestMap();
        var listener = Server.System<SpawnLocationListenerSystem>();
        EntityUid rule = default;

        await Server.WaitPost(() =>
        {
            STicker.SetGamePreset((GamePresetPrototype?) null);
            rule = STicker.AddGameRule(ParadoxRule);
            SEntMan.EnsureComponent<TestListenerComponent>(rule);
            // A valid location makes an unwanted attempt observable without a log-induced fixture failure.
            SComp<AntagRandomSpawnComponent>(rule).Coords = map.GridCoords;
            Assert.That(SComp<AntagSelectionComponent>(rule).SelectionTime, Is.EqualTo(AntagSelectionTime.Never));

            // With no living players in the lobby, Paradox's real Started handler ends the rule.
            Assert.That(STicker.StartGameRule(rule), Is.True);
            Assert.That(SEntMan.HasComponent<EndedGameRuleComponent>(rule), Is.True);
            Assert.That(STicker.IsGameRuleAdded(rule), Is.False);
            Assert.That(listener.Count(rule), Is.Zero);

            // This raises RulePlayerSpawningEvent through the real round-start path.
            STicker.StartRound(true);
        });

        await Server.WaitAssertion(() =>
        {
            Assert.That(STicker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(SComp<AntagSelectionComponent>(rule).PreSelectionsComplete, Is.True);
            Assert.That(listener.Count(rule), Is.Zero, "An ended Paradox rule must not request a ghost spawn location.");
            Assert.That(SEntMan.EntityQuery<GhostRoleAntagSpawnerComponent>().Any(spawner => spawner.Rule == rule), Is.False);
        });
    }

    [Test]
    public async Task AddedInactiveGhostRuleCanSpawn()
    {
        var map = await Pair.CreateTestMap();
        var listener = Server.System<SpawnLocationListenerSystem>();
        EntityUid rule = default;

        await Server.WaitPost(() =>
        {
            rule = STicker.AddGameRule(ParadoxRule);
            SEntMan.EnsureComponent<TestListenerComponent>(rule);
            SComp<AntagRandomSpawnComponent>(rule).Coords = map.GridCoords;
            Assert.That(STicker.IsGameRuleAdded(rule), Is.True);
            Assert.That(STicker.IsGameRuleActive(rule), Is.False);
            Assert.That(SEntMan.HasComponent<EndedGameRuleComponent>(rule), Is.False);

            var antag = SProtoMan.Index(SComp<AntagSelectionComponent>(rule).Antags.Single().Proto);
            AntagSys.SpawnGhostRole((rule, SComp<AntagSelectionComponent>(rule)), antag, assert: true);
        });

        await Server.WaitAssertion(() =>
        {
            Assert.That(listener.Count(rule), Is.EqualTo(1));
            Assert.That(SEntMan.EntityQuery<GhostRoleAntagSpawnerComponent>().Count(spawner => spawner.Rule == rule), Is.EqualTo(1));
            Assert.That(STicker.IsGameRuleAdded(rule), Is.True);
            Assert.That(STicker.IsGameRuleActive(rule), Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MidroundDurationConditionRejectsLobby(bool restart)
    {
        // Scale the Dynamic min-duration gate down instead of simulating ten minutes of game ticks.
        var condition = new RoundDurationCondition { Min = TimeSpan.FromTicks(1) };
        var root = new EntSelector { Id = ParadoxRule };
        var context = new EntityTableContext();

        if (restart)
        {
            await Server.WaitPost(() =>
            {
                STicker.SetGamePreset((GamePresetPrototype?) null);
                STicker.StartRound(true);
            });
            await Pair.RunTicksSync(1);
            await Server.WaitPost(() =>
            {
                Assert.That(STicker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
                Assert.That(condition.Evaluate(root, SEntMan, SProtoMan, context), Is.True);
                STicker.EndRound(force: true);
                Assert.That(STicker.RunLevel, Is.EqualTo(GameRunLevel.PostRound));
                Assert.That(condition.Evaluate(root, SEntMan, SProtoMan, context), Is.True,
                    "Post-round duration must remain available.");
                STicker.RestartRound();
            });
        }

        await Server.WaitAssertion(() =>
        {
            Assert.That(STicker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby));
            Assert.That(Server.ResolveDependency<IGameTiming>().CurTime - STicker.RoundStartTimeSpan, Is.GreaterThanOrEqualTo(condition.Min),
                "The fixture must expose stale elapsed time to the duration condition.");
            Assert.That(condition.Evaluate(root, SEntMan, SProtoMan, context), Is.False,
                "Lobby uptime or previous-round duration must not satisfy a midround Dynamic gate.");
        });
    }
}
