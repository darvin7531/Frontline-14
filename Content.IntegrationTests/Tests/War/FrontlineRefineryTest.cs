using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Stack;
using Content.Server.War;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
[TestOf(typeof(FrontlineRefinerySystem))]
public sealed class FrontlineRefineryTest : GameTest
{
    private sealed partial class ReentrantStackMutationSystem : EntitySystem
    {
        public EntityUid Target;
        public bool Enabled;
        public bool RestoreCurrent;

        public override void Initialize()
        {
            SubscribeLocalEvent<StackComponent, StackCountChangedEvent>(OnStackCountChanged);
        }

        private void OnStackCountChanged(Entity<StackComponent> stack, ref StackCountChangedEvent args)
        {
            if (!Enabled)
                return;

            Enabled = false;
            if (RestoreCurrent)
            {
                RestoreCurrent = false;
                EntityManager.System<StackSystem>().SetCount(stack, args.OldCount);
                return;
            }

            if (stack.Owner == Target || !Exists(Target))
                return;

            Del(Target);
        }
    }

    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: TestPersistedFrontlineRefinery
          components:
          - type: FrontlineRefinery
            jobs:
            - recipe: FrontlineSteel
              remaining: 1

        - type: entity
          id: TestInvalidPersistedFrontlineRefinery
          components:
          - type: FrontlineRefinery
            jobs:
            - recipe: TestInvalidFrontlineRecipe
              remaining: 0

        - type: frontlineRefineryRecipe
          id: TestInvalidFrontlineRecipe
          input:
            FrontlineRawIron: 0
          output:
            Steel: 5
          duration: 1

        - type: frontlineRefineryRecipe
          id: TestAbstractStackFrontlineRecipe
          input:
            BaseMediumStack: 1
          output:
            Steel: 1
          duration: 1

        - type: frontlineRefineryRecipe
          id: TestMultiOutputRecipe
          input:
            FrontlineRawIron: 1
          output:
            Steel: 1
            TechnologyAlloy: 1
          duration: 1

        """;

    [Test]
    public async Task FrontlineMaterialsDoNotHaveVanillaOreTag()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var tagSystem = server.System<TagSystem>();
        EntityUid rawIron = default;
        EntityUid rawTechnology = default;
        EntityUid technologyAlloy = default;
        EntityUid vanillaOre = default;

        await server.WaitPost(() =>
        {
            rawIron = SEntMan.SpawnEntity("FrontlineRawIron1", map.GridCoords);
            rawTechnology = SEntMan.SpawnEntity("RawTechnologyMaterial1", map.GridCoords);
            technologyAlloy = SEntMan.SpawnEntity("TechnologyAlloy1", map.GridCoords);
            vanillaOre = SEntMan.SpawnEntity("SteelOre1", map.GridCoords);
        });

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(tagSystem.HasTag(rawIron, "Ore"), Is.False);
                Assert.That(tagSystem.HasTag(rawTechnology, "Ore"), Is.False);
                Assert.That(tagSystem.HasTag(technologyAlloy, "Ore"), Is.False);
                Assert.That(tagSystem.HasTag(vanillaOre, "Ore"), Is.True);
            });
        });
    }

    [Test]
    public async Task IronJobConsumesInputAndProducesSteelAfterDelay()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        EntityUid input = default;
        bool accepted = false;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            accepted = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
        });

        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(SEntMan.EntityExists(input), Is.False);
            Assert.That(CountStacks("Steel"), Is.Zero);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Has.Count.EqualTo(1));
        });

        await Pair.RunSeconds(4.9f);
        await server.WaitAssertion(() => Assert.That(CountStacks("Steel"), Is.Zero));

        await Pair.RunSeconds(0.2f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("Steel"), Is.EqualTo(5));
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task TechnologyMaterialProducesDistinctTechnologyAlloy()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        bool accepted = false;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(5, "RawTechnologyMaterial", map.GridCoords);
            accepted = refinerySystem.TrySubmitJob(refinery, "FrontlineTechnologyAlloy", new[] { input });
        });

        await Pair.RunSeconds(10.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(CountStacks("RawTechnologyMaterial"), Is.Zero);
            Assert.That(CountStacks("TechnologyAlloy"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RejectedRequestsDoNotConsumeInput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        bool insufficient = true;
        bool missing = true;
        bool invalid = true;
        bool multiOutput = true;

        await server.WaitPost(() =>
        {
            var refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(4, "FrontlineRawIron", map.GridCoords);
            insufficient = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
            missing = refinerySystem.TrySubmitJob(refinery, "MissingFrontlineRecipe", new[] { input });
            invalid = refinerySystem.TrySubmitJob(refinery, "TestInvalidFrontlineRecipe", new[] { input });
            multiOutput = refinerySystem.TrySubmitJob(refinery, "TestMultiOutputRecipe", new[] { input });
        });

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(insufficient, Is.False);
                Assert.That(missing, Is.False);
                Assert.That(invalid, Is.False);
                Assert.That(multiOutput, Is.False);
                Assert.That(refinerySystem.GetAvailableRecipes().Select(recipe => recipe.ID),
                    Does.Not.Contain("TestInvalidFrontlineRecipe")
                        .And.Not.Contain("TestAbstractStackFrontlineRecipe")
                        .And.Not.Contain("TestMultiOutputRecipe"));
                Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(4));
                Assert.That(CountStacks("Steel"), Is.Zero);
            });
        });
    }

    [Test]
    public async Task ConcurrentRequestsCannotDuplicateMaterialsOrOutput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        bool first = false;
        bool second = true;

        await server.WaitPost(() =>
        {
            var refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            first = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
            second = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
        });

        await Pair.RunSeconds(5.1f);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(first, Is.True);
                Assert.That(second, Is.False);
                Assert.That(CountStacks("FrontlineRawIron"), Is.Zero);
                Assert.That(CountStacks("Steel"), Is.EqualTo(5));
            });
        });
    }

    [Test]
    public async Task MultipleJobsProcessIndependently()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        bool first = false;
        bool second = false;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(10, "FrontlineRawIron", map.GridCoords);
            first = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
            second = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(first && second, Is.True);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Has.Count.EqualTo(2));
        });

        await Pair.RunSeconds(5.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("Steel"), Is.EqualTo(10));
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task PresetJobStateResumesWithoutReset()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        _ = server.System<FrontlineRefinerySystem>();
        EntityUid refinery = default;

        await server.WaitPost(() => refinery = SEntMan.SpawnEntity("TestPersistedFrontlineRefinery", map.GridCoords));
        await server.WaitAssertion(() =>
        {
            var job = SComp<FrontlineRefineryComponent>(refinery).Jobs.Single();
            Assert.That(job.Remaining, Is.EqualTo(TimeSpan.FromSeconds(1)));
        });

        await Pair.RunSeconds(0.5f);
        await server.WaitAssertion(() => Assert.That(CountStacks("Steel"), Is.Zero));

        await Pair.RunSeconds(0.6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("Steel"), Is.EqualTo(5));
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task ServerApiExposesRecipesAndQueueState()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            Assert.That(refinerySystem.GetJobs(refinery), Is.Empty);
            Assert.That(refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input }), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(refinerySystem.GetAvailableRecipes().Select(recipe => recipe.ID),
                Is.SupersetOf(new[] { "FrontlineSteel", "FrontlineTechnologyAlloy" }));
            var snapshot = refinerySystem.GetJobs(refinery);
            Assert.That(snapshot, Has.Count.EqualTo(1));
            snapshot[0].Remaining = TimeSpan.Zero;
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs.Single().Remaining,
                Is.EqualTo(TimeSpan.FromSeconds(5)));
        });
    }

    [Test]
    public async Task ReentrantInputMutationRollsBackConsumedStacks()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        var mutationSystem = server.System<ReentrantStackMutationSystem>();
        EntityUid refinery = default;
        bool accepted = true;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var first = stackSystem.SpawnAtPosition(3, "FrontlineRawIron", map.GridCoords);
            var second = stackSystem.SpawnAtPosition(2, "FrontlineRawIron", map.GridCoords);
            mutationSystem.Target = second;
            mutationSystem.Enabled = true;
            accepted = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { first, second });
        });

        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(3));
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task FinalInputEventTerminatingRefineryRollsBackConsumption()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        var mutationSystem = server.System<ReentrantStackMutationSystem>();
        bool accepted = true;

        await server.WaitPost(() =>
        {
            var refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            mutationSystem.Target = refinery;
            mutationSystem.Enabled = true;
            accepted = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
        });

        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task ReentrantCurrentStackRestoreRejectsWithoutDuplication()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        var mutationSystem = server.System<ReentrantStackMutationSystem>();
        EntityUid refinery = default;
        bool accepted = true;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            mutationSystem.RestoreCurrent = true;
            mutationSystem.Enabled = true;
            accepted = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input });
        });

        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(5));
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task InvalidPersistedJobsRemainQueuedWithoutOutput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        _ = server.System<FrontlineRefinerySystem>();
        EntityUid refinery = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("TestInvalidPersistedFrontlineRefinery", map.GridCoords);
            SComp<FrontlineRefineryComponent>(refinery).Jobs.Add(new FrontlineRefineryJob
            {
                Recipe = "MissingFrontlineRecipe",
                Remaining = TimeSpan.Zero,
            });
        });
        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Has.Count.EqualTo(2));
            Assert.That(CountStacks("Steel"), Is.Zero);
        });
    }

    [Test]
    public async Task PausedMapDoesNotAdvanceJobs()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var mapSystem = server.System<SharedMapSystem>();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            Assert.That(refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input }), Is.True);
            mapSystem.SetPaused(map.MapId, true);
        });

        await Pair.RunSeconds(6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("Steel"), Is.Zero);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs.Single().Remaining,
                Is.EqualTo(TimeSpan.FromSeconds(5)));
        });

        await server.WaitPost(() => mapSystem.SetPaused(map.MapId, false));
        await Pair.RunSeconds(5.1f);
        await server.WaitAssertion(() => Assert.That(CountStacks("Steel"), Is.EqualTo(5)));
    }

    private int CountStacks(string stackType)
    {
        return SEntMan.EntityQuery<StackComponent>()
            .Where(stack => stack.StackTypeId == stackType)
            .Sum(stack => stack.Count);
    }
}
