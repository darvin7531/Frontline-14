using System.Collections.Generic;
using System.Linq;
using Content.Client.War;
using Content.IntegrationTests.Tests.Interaction;
using Robust.Client.UserInterface.Controls;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Stack;
using Content.Server.War;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.UserInterface;
using Content.Shared.War;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
[TestOf(typeof(FrontlineRefinerySystem))]
public sealed class FrontlineRefineryTest : GameTest
{
    private static readonly ProtoId<TagPrototype> OreTag = "Ore";

    public sealed partial class ReentrantStackMutationSystem : EntitySystem
    {
        public EntityUid Target;
        public bool Enabled;
        public bool RestoreCurrent;
        public int SkipEvents;
        public Action PriceMutation;

        public override void Initialize()
        {
            SubscribeLocalEvent<StackComponent, StackCountChangedEvent>(OnStackCountChanged);
        }

        private void OnStackCountChanged(Entity<StackComponent> stack, ref StackCountChangedEvent args)
        {
            if (!Enabled)
                return;

            if (SkipEvents > 0)
            {
                SkipEvents--;
                return;
            }
            Enabled = false;
            if (RestoreCurrent)
            {
                RestoreCurrent = false;
                EntityManager.System<StackSystem>().SetCount(stack, args.OldCount);
                return;
            }
            if (PriceMutation != null)
            {
                var mutation = PriceMutation;
                PriceMutation = null;
                mutation();
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

        - type: entity
          id: TestTwoSlotFrontlineRefinery
          components:
          - type: FrontlineRefinery
            processingSlots: 2

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
          id: TestBatchMultiInputRecipe
          input:
            FrontlineRawIron: 3
            RawTechnologyMaterial: 2
          output:
            BasicMaterials: 5
          duration: 5

        - type: frontlineRefineryRecipe
          id: TestMultiOutputRecipe
          input:
            FrontlineRawIron: 1
          output:
            Steel: 1
            TechnologyAlloy: 1
          duration: 1

        """;

    [TestCase(false)]
    [TestCase(true)]
    public async Task BatchAllUsesMinimumFullMultiInputRecipeAndPreservesRemainders(bool missing)
    {
        var map = await Pair.CreateTestMap();
        var system = Pair.Server.System<FrontlineRefinerySystem>();
        var stack = Pair.Server.System<StackSystem>();
        EntityUid machine = default;
        await Pair.Server.WaitPost(() =>
        {
            machine = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            Assert.That(system.TryInsertInput(machine, stack.SpawnAtPosition(11, "FrontlineRawIron", map.GridCoords)), Is.True);
            if (!missing)
                Assert.That(system.TryInsertInput(machine, stack.SpawnAtPosition(5, "RawTechnologyMaterial", map.GridCoords)), Is.True);
            Assert.That(system.TrySubmitContainedJob(machine, "TestBatchMultiInputRecipe", 1, true), Is.EqualTo(!missing));
            Assert.That(system.TrySubmitContainedJob(machine, "TestBatchMultiInputRecipe", 1, true), Is.False);
        });
        await Pair.Server.WaitAssertion(() =>
        {
            var state = system.BuildUiState(machine);
            Assert.That(state.Inputs.Single(i => i.Stack == "FrontlineRawIron").Amount, Is.EqualTo(missing ? 11 : 5));
            Assert.That(state.Jobs.Sum(j => j.Batches), Is.EqualTo(missing ? 0 : 2));
            if (!missing)
                Assert.That(state.Inputs.Single(i => i.Stack == "RawTechnologyMaterial").Amount, Is.EqualTo(1));
        });
        await Pair.RunSeconds(10.2f);
        await Pair.Server.WaitAssertion(() => Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(missing ? 0 : 10)));
    }

    [Test]
    public async Task CompactPaidBatchesAdvanceTogetherWithoutExpansion()
    {
        var map = await Pair.CreateTestMap();
        var system = Pair.Server.System<FrontlineRefinerySystem>();
        EntityUid machine = default;
        await Pair.Server.WaitPost(() =>
        {
            machine = SEntMan.SpawnEntity("TestTwoSlotFrontlineRefinery", map.GridCoords);
            var input = Pair.Server.System<StackSystem>().SpawnAtPosition(17, "FrontlineRawIron", map.GridCoords);
            Assert.That(system.TryInsertInput(machine, input), Is.True);
            Assert.That(system.TrySubmitContainedJob(machine, "FrontlineSteel", 3), Is.True);
            Assert.That(system.GetJobs(machine), Has.Count.EqualTo(1), "Pending batches are compact, not a client-sized allocation.");
        });
        await Pair.RunSeconds(2.5f);
        await Pair.Server.WaitAssertion(() =>
        {
            var jobs = system.GetJobs(machine);
            Assert.That(jobs, Has.Count.EqualTo(1), "Parallel batches retain one bounded ledger row.");
            Assert.That(jobs.Single().Batches, Is.EqualTo(3));
            Assert.That(jobs.Single().Remaining.TotalSeconds, Is.EqualTo(2.5).Within(0.2));
        });
        await Pair.RunSeconds(2.6f);
        await Pair.Server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(15));
            Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(2));
            Assert.That(system.GetJobs(machine), Is.Empty);
        });
    }

    [Test]
    public async Task LargeReadyBatchMaterializesAtMostThirtyTwoBatchesPerTick()
    {
        var map = await Pair.CreateTestMap();
        var system = Pair.Server.System<FrontlineRefinerySystem>();
        EntityUid machine = default;
        await Pair.Server.WaitPost(() =>
        {
            machine = SEntMan.SpawnEntity("TestPersistedFrontlineRefinery", map.GridCoords);
            var job = SComp<FrontlineRefineryComponent>(machine).Jobs.Single();
            job.Batches = 100;
            job.Remaining = TimeSpan.Zero;
        });
        for (var tick = 1; tick <= 4; tick++)
        {
            await Pair.RunTicksSync(1);
            await Pair.Server.WaitAssertion(() =>
            {
                var finished = Math.Min(tick * 32, 100);
                Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(finished * 5));
                Assert.That(system.GetJobs(machine).Sum(job => job.Batches), Is.EqualTo(100 - finished));
            });
        }
    }

    [Test]
    public void VersionSixReaderPreservesOldSingleBatchClaimsAndRejectsMissingNewClaims()
    {
        var old = """
            {"SnapshotVersion":6,"WarId":42,"Bases":[],"Resources":[],"Factories":[],"Vehicles":[],
             "Refineries":[{"RefineryId":"machine","Prototype":"FrontlineRefinery",
              "Jobs":[{"Recipe":"FrontlineSteel","RemainingTicks":123}],"Inputs":[],"Outputs":[]}]}
            """;
        using var document = System.Text.Json.JsonDocument.Parse(old);
        var upgraded = WarStrategicSnapshotSystem.ReadSnapshot(document.RootElement);
        Assert.That(upgraded.SnapshotVersion, Is.EqualTo(8));
        var job = upgraded.Refineries.Single().Jobs.Single();
        Assert.That((job.Recipe, job.RemainingTicks, job.Batches), Is.EqualTo(("FrontlineSteel", 123L, 1L)));
        Assert.That(job.Claim, Is.Not.Null);
        Assert.That(job.Claim!.Legacy, Is.True);
        Assert.That(job.Claim.Owner, Is.Null);
        Assert.That(job.Claim.PaidInputs, Is.Empty);
        using var missingClaim = System.Text.Json.JsonDocument.Parse(old.Replace("\"SnapshotVersion\":6", "\"SnapshotVersion\":8")
            .Replace("\"RemainingTicks\":123}", "\"RemainingTicks\":123,\"Batches\":1}"));
        Assert.Throws<System.Text.Json.JsonException>(() => WarStrategicSnapshotSystem.ReadSnapshot(missingClaim.RootElement));
        using var current = System.Text.Json.JsonDocument.Parse(old.Replace("\"SnapshotVersion\":6", "\"SnapshotVersion\":7")
            .Replace("\"RemainingTicks\":123}", "\"RemainingTicks\":123,\"Batches\":1}"));
        Assert.That(WarStrategicSnapshotSystem.ReadSnapshot(current.RootElement).SnapshotVersion, Is.EqualTo(8));
    }

    [Test]
    [NonParallelizable]
    public async Task RefineryPaidReceiptFreezesEveryInputBeforeDebitCallback()
    {
        var map = await Pair.CreateTestMap();
        var system = Pair.Server.System<FrontlineRefinerySystem>();
        var mutation = Pair.Server.System<ReentrantStackMutationSystem>();
        var recipe = Pair.Server.ProtoMan.Index(new ProtoId<FrontlineRefineryRecipePrototype>("TestBatchMultiInputRecipe"));
        var original = recipe.Input.ToDictionary(entry => entry.Key, entry => entry.Value);
        EntityUid machine = default;
        try
        {
            await Pair.Server.WaitPost(() =>
            {
                machine = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
                var iron = Pair.Server.System<StackSystem>().SpawnAtPosition(11, "FrontlineRawIron", map.GridCoords);
                var technology = Pair.Server.System<StackSystem>().SpawnAtPosition(5, "RawTechnologyMaterial", map.GridCoords);
                mutation.SkipEvents = 0;
                mutation.RestoreCurrent = false;
                mutation.PriceMutation = () => recipe.Input["RawTechnologyMaterial"] = 7;
                mutation.Enabled = true;
                Assert.That(system.TrySubmitJob(machine, recipe.ID, new[] { iron, technology }, 2), Is.True);
                Assert.That(SComp<StackComponent>(iron).Count, Is.EqualTo(5));
                Assert.That(SComp<StackComponent>(technology).Count, Is.EqualTo(1));
            });
            await Pair.Server.WaitAssertion(() => Assert.That(
                SComp<FrontlineRefineryComponent>(machine).Jobs.Single().PaidInputs.OrderBy(entry => entry.Key.Id),
                Is.EqualTo(original.OrderBy(entry => entry.Key.Id))));
        }
        finally
        {
            await Pair.Server.WaitPost(() =>
            {
                mutation.Enabled = false;
                mutation.PriceMutation = null;
                recipe.Input = original;
            });
        }
    }

    [Test]
    public async Task ValidInputCanBeInsertedAndRemainsPhysical()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        EntityUid input = default;
        bool inserted = false;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            inserted = refinerySystem.TryInsertInput(refinery, input);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(inserted, Is.True);
            Assert.That(SEntMan.EntityExists(input), Is.True);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).InputContainer.Contains(input), Is.True);
        });
    }

    [Test]
    public async Task ProductionRefineryExposesPlayerInterface()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        EntityUid refinery = default;

        await server.WaitPost(() => refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords));

        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<ActivatableUIComponent>(refinery), Is.True);
            Assert.That(SEntMan.HasComponent<UserInterfaceComponent>(refinery), Is.True);
        });
    }

    [Test]
    public void ProductionRecipesHaveLocalizedDisplayNames()
    {
        var localization = Pair.Server.ResolveDependency<ILocalizationManager>();

        foreach (var recipe in Pair.Server.System<FrontlineRefinerySystem>().GetAvailableRecipes())
        {
            Assert.That(recipe.Name.ToString(), Is.Not.EqualTo(recipe.ID));
            Assert.That(localization.HasString(recipe.Name), Is.True);
        }
    }

    [Test]
    public async Task InvalidItemIsRejectedFromInput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        EntityUid refinery = default;
        EntityUid item = default;
        bool inserted = true;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            item = SEntMan.SpawnEntity("Screwdriver", map.GridCoords);
            inserted = refinerySystem.TryInsertInput(refinery, item);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(inserted, Is.False);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).InputContainer.Contains(item), Is.False);
            Assert.That(SEntMan.EntityExists(item), Is.True);
        });
    }

    [Test]
    public async Task EjectReturnsSamePhysicalInputWithoutDuplication()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        EntityUid input = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            Assert.That(refinerySystem.TryInsertInput(refinery, input), Is.True);
            refinerySystem.EjectInputs(refinery);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).InputContainer.ContainedEntities, Is.Empty);
            Assert.That(SEntMan.EntityExists(input), Is.True);
            Assert.That(SComp<StackComponent>(input).Count, Is.EqualTo(5));
            Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task RemotePlayerCannotEjectInput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        EntityUid input = default;
        EntityUid player = default;
        var ejected = true;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            player = SEntMan.SpawnEntity(null, map.GridCoords.Offset(new Vector2i(10, 0)));
            Assert.That(refinerySystem.TryInsertInput(refinery, input), Is.True);

            ejected = refinerySystem.TryEjectPlayerInputs(refinery, player);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(ejected, Is.False);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).InputContainer.Contains(input), Is.True);
        });
    }

    [Test]
    public async Task SubmissionOnlyConsumesContainedInput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        EntityUid contained = default;
        EntityUid outside = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            contained = stackSystem.SpawnAtPosition(10, "FrontlineRawIron", map.GridCoords);
            outside = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            Assert.That(refinerySystem.TryInsertInput(refinery, contained), Is.True);
            Assert.That(refinerySystem.TrySubmitContainedJob(refinery, "FrontlineSteel"), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(SComp<StackComponent>(contained).Count, Is.EqualTo(5));
            Assert.That(SComp<StackComponent>(outside).Count, Is.EqualTo(5));
            Assert.That(refinerySystem.GetJobs(refinery), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task FrontlineMaterialsDoNotHaveVanillaOreTag()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var tagSystem = server.System<TagSystem>();
        EntityUid rawIron = default;
        EntityUid basicMaterials = default;
        EntityUid rawTechnology = default;
        EntityUid technologyAlloy = default;
        EntityUid vanillaOre = default;

        await server.WaitPost(() =>
        {
            rawIron = SEntMan.SpawnEntity("FrontlineRawIron1", map.GridCoords);
            basicMaterials = SEntMan.SpawnEntity("BasicMaterials1", map.GridCoords);
            rawTechnology = SEntMan.SpawnEntity("RawTechnologyMaterial1", map.GridCoords);
            technologyAlloy = SEntMan.SpawnEntity("TechnologyAlloy1", map.GridCoords);
            vanillaOre = SEntMan.SpawnEntity("SteelOre1", map.GridCoords);
        });

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(tagSystem.HasTag(rawIron, OreTag), Is.False);
                Assert.That(tagSystem.HasTag(basicMaterials, OreTag), Is.False);
                Assert.That(SComp<StackComponent>(basicMaterials).StackTypeId, Is.EqualTo("BasicMaterials"));
                Assert.That(tagSystem.HasTag(rawTechnology, OreTag), Is.False);
                Assert.That(tagSystem.HasTag(technologyAlloy, OreTag), Is.False);
                Assert.That(tagSystem.HasTag(vanillaOre, OreTag), Is.True);
            });
        });
    }

    [Test]
    public async Task IronJobConsumesInputAndProducesBasicMaterialsAfterDelay()
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
            Assert.That(CountStacks("BasicMaterials"), Is.Zero);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Has.Count.EqualTo(1));
        });

        await Pair.RunSeconds(4.9f);
        await server.WaitAssertion(() => Assert.That(CountStacks("BasicMaterials"), Is.Zero));

        await Pair.RunSeconds(0.2f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5));
            Assert.That(CountStacks("Steel"), Is.Zero);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task IronJobRetainsBasicMaterialsInOutputContainerAfterDelay()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        var containers = server.System<SharedContainerSystem>();
        EntityUid refinery = default;
        EntityUid input = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            Assert.That(refinerySystem.TryInsertInput(refinery, input), Is.True);
            Assert.That(refinerySystem.TrySubmitContainedJob(refinery, "FrontlineSteel"), Is.True);
        });

        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(input), Is.False);
            Assert.That(CountStacks("FrontlineRawIron"), Is.Zero);
            Assert.That(refinerySystem.GetJobs(refinery), Has.Count.EqualTo(1));
            Assert.That(CountStacks("BasicMaterials"), Is.Zero);
        });

        await Pair.RunSeconds(4.9f);
        await server.WaitAssertion(() => Assert.That(CountStacks("BasicMaterials"), Is.Zero));

        await Pair.RunSeconds(0.2f);
        await server.WaitAssertion(() =>
        {
            Assert.That(refinerySystem.GetJobs(refinery), Is.Empty);
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5));
            Assert.That(containers.TryGetContainer(refinery, "outputContainer", out var output), Is.True,
                "Completed BasicMaterials must be retained in refinery outputContainer.");
            Assert.That(output.ContainedEntities, Has.Count.EqualTo(1));
            var stack = SComp<StackComponent>(output.ContainedEntities.Single());
            Assert.That(stack.StackTypeId, Is.EqualTo("BasicMaterials"));
            Assert.That(stack.Count, Is.EqualTo(5));
            var query = SEntMan.EntityQueryEnumerator<StackComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var candidate, out var transform))
            {
                if (candidate.StackTypeId == "BasicMaterials" && transform.MapID == map.MapId)
                    Assert.That(containers.IsEntityInContainer(uid), Is.True, "Material output must not be loose on the floor.");
            }
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
                Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5));
            });
        });
    }

    [Test]
    public async Task RefineryRunsAllPaidJobsInParallel()
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

        await Pair.RunSeconds(2.5f);
        await server.WaitAssertion(() =>
        {
            var jobs = refinerySystem.GetJobs(refinery);
            Assert.That(jobs, Has.Count.EqualTo(2));
            Assert.That(jobs.Select(job => job.Remaining.TotalSeconds), Is.All.EqualTo(2.5).Within(0.2));
            Assert.That(CountStacks("BasicMaterials"), Is.Zero);
        });

        await Pair.RunSeconds(2.6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(10));
            Assert.That(refinerySystem.GetJobs(refinery), Is.Empty);
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
        await server.WaitAssertion(() => Assert.That(CountStacks("BasicMaterials"), Is.Zero));

        await Pair.RunSeconds(0.6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5));
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task MapperSlotSettingDoesNotLimitParallelRefineryJobs()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("TestTwoSlotFrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(15, "FrontlineRawIron", map.GridCoords);
            Assert.That(refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input }), Is.True);
            Assert.That(refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input }), Is.True);
            Assert.That(refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input }), Is.True);
        });

        await Pair.RunSeconds(2.5f);
        await server.WaitAssertion(() =>
        {
            var jobs = refinerySystem.GetJobs(refinery);
            Assert.That(jobs[0].Remaining.TotalSeconds, Is.EqualTo(2.5).Within(0.2));
            Assert.That(jobs[1].Remaining.TotalSeconds, Is.EqualTo(2.5).Within(0.2));
            Assert.That(jobs[2].Remaining.TotalSeconds, Is.EqualTo(2.5).Within(0.2));
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
    public async Task ServerUiStateReflectsContainedInputAndQueue()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            var input = stackSystem.SpawnAtPosition(10, "FrontlineRawIron", map.GridCoords);
            Assert.That(refinerySystem.TryInsertInput(refinery, input), Is.True);
            Assert.That(refinerySystem.TrySubmitContainedJob(refinery, "FrontlineSteel"), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            var state = refinerySystem.BuildUiState(refinery);
            Assert.That(state.Inputs.Single(input => input.Stack == "FrontlineRawIron").Amount, Is.EqualTo(5));
            Assert.That(state.Recipes.Single(recipe => recipe.Id == "FrontlineSteel").CanSubmit, Is.True);
            Assert.That(state.Jobs, Has.Length.EqualTo(1));
            Assert.That(state.Jobs[0].Processing, Is.True);
            Assert.That(state.Jobs[0].Remaining, Is.EqualTo(TimeSpan.FromSeconds(5)));
        });
    }

    [Test]
    public async Task RemotePlayerSubmissionIsRejected()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var refinerySystem = server.System<FrontlineRefinerySystem>();
        var stackSystem = server.System<StackSystem>();
        EntityUid refinery = default;
        EntityUid input = default;
        bool accepted = true;

        await server.WaitPost(() =>
        {
            refinery = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            input = stackSystem.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2i(10, 0)));
            Assert.That(refinerySystem.TryInsertInput(refinery, input), Is.True);
            accepted = refinerySystem.TrySubmitPlayerJob(refinery, user, "FrontlineSteel");
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(refinerySystem.GetJobs(refinery), Is.Empty);
            Assert.That(SComp<StackComponent>(input).Count, Is.EqualTo(5));
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

    [TestCase(1)]
    [TestCase(2)]
    public async Task ReentrantCurrentStackRestoreRejectsWithoutDuplication(int batches)
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
            var input = stackSystem.SpawnAtPosition(5 * batches, "FrontlineRawIron", map.GridCoords);
            mutationSystem.RestoreCurrent = true;
            mutationSystem.Enabled = true;
            accepted = refinerySystem.TrySubmitJob(refinery, "FrontlineSteel", new[] { input }, batches);
        });

        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(5 * batches));
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs, Is.Empty);
        });
    }

    [Test]
    public async Task BatchBehindQuarantinedJobDoesNotLosePaidBatches()
    {
        var map = await Pair.CreateTestMap();
        EntityUid machine = default;
        await Pair.Server.WaitPost(() =>
        {
            machine = SEntMan.SpawnEntity("TestInvalidPersistedFrontlineRefinery", map.GridCoords);
            SComp<FrontlineRefineryComponent>(machine).Jobs.Add(new FrontlineRefineryJob
            {
                Recipe = "FrontlineSteel", Remaining = TimeSpan.FromSeconds(5), Batches = 2,
            });
        });
        await Pair.RunSeconds(5.1f);
        await Pair.Server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(10));
            Assert.That(SComp<FrontlineRefineryComponent>(machine).Jobs.Where(j => j.Recipe == "FrontlineSteel"), Is.Empty);
        });
        await Pair.RunSeconds(5.1f);
        await Pair.Server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(10));
            Assert.That(SComp<FrontlineRefineryComponent>(machine).Jobs.Single().Recipe.Id, Is.EqualTo("TestInvalidFrontlineRecipe"));
        });
    }

    [Test]
    public async Task LaterBatchInputCallbackRollsBackEveryDebitAndAllowsFundedRetry()
    {
        var map = await Pair.CreateTestMap();
        var system = Pair.Server.System<FrontlineRefinerySystem>();
        var stacks = Pair.Server.System<StackSystem>();
        var mutation = Pair.Server.System<ReentrantStackMutationSystem>();
        EntityUid machine = default;
        EntityUid iron = default;
        EntityUid technology = default;
        await Pair.Server.WaitPost(() =>
        {
            machine = SEntMan.SpawnEntity("FrontlineRefinery", map.GridCoords);
            iron = stacks.SpawnAtPosition(11, "FrontlineRawIron", map.GridCoords);
            technology = stacks.SpawnAtPosition(5, "RawTechnologyMaterial", map.GridCoords);
            Assert.That(system.TryInsertInput(machine, iron), Is.True);
            Assert.That(system.TryInsertInput(machine, technology), Is.True);
            mutation.SkipEvents = 1;
            mutation.RestoreCurrent = true;
            mutation.Enabled = true;
            Assert.That(system.TrySubmitContainedJob(machine, "TestBatchMultiInputRecipe", 2), Is.False);
        });
        await Pair.Server.WaitAssertion(() =>
        {
            Assert.That(SComp<StackComponent>(iron).Count, Is.EqualTo(11));
            Assert.That(SComp<StackComponent>(technology).Count, Is.EqualTo(5));
            Assert.That(SComp<FrontlineRefineryComponent>(machine).InputContainer.ContainedEntities, Is.EquivalentTo(new[] { iron, technology }));
            Assert.That(system.GetJobs(machine), Is.Empty);
        });
        await Pair.Server.WaitPost(() => Assert.That(system.TrySubmitContainedJob(machine, "TestBatchMultiInputRecipe", 2), Is.True));
        await Pair.RunSeconds(10.2f);
        await Pair.Server.WaitAssertion(() =>
        {
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(10));
            Assert.That(CountStacks("FrontlineRawIron"), Is.EqualTo(5));
            Assert.That(CountStacks("RawTechnologyMaterial"), Is.EqualTo(1));
            Assert.That(system.GetJobs(machine), Is.Empty);
        });
    }

    [Test]
    public async Task InvalidPersistedJobIsQuarantinedWithoutBlockingValidJob()
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
                Recipe = "FrontlineSteel",
                Remaining = TimeSpan.FromSeconds(0.1),
            });
        });
        await Pair.RunSeconds(0.2f);
        await server.WaitAssertion(() =>
        {
            var jobs = SComp<FrontlineRefineryComponent>(refinery).Jobs;
            Assert.That(jobs, Has.Count.EqualTo(1), "Keep the invalid paid claim for recovery, not silent deletion.");
            Assert.That(jobs[0].Recipe.Id, Is.EqualTo("TestInvalidFrontlineRecipe"));
            Assert.That(jobs[0].Remaining, Is.EqualTo(TimeSpan.Zero));
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5));
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
            Assert.That(CountStacks("BasicMaterials"), Is.Zero);
            Assert.That(SComp<FrontlineRefineryComponent>(refinery).Jobs.Single().Remaining,
                Is.EqualTo(TimeSpan.FromSeconds(5)));
        });

        await server.WaitPost(() => mapSystem.SetPaused(map.MapId, false));
        await Pair.RunSeconds(5.1f);
        await server.WaitAssertion(() => Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5)));
    }

    public sealed class OutputBuiTest : InteractionTest
    {
        [Test]
        public async Task SevenPaidIronJobsConserveMaterialsThroughPlayerWithdrawal()
        {
            await SpawnTarget("FrontlineRefinery");
            var refinery = STarget!.Value;
            var system = Server.System<FrontlineRefinerySystem>();
            var containers = Server.System<SharedContainerSystem>();
            BaseContainer output = default!;
            Robust.Shared.Containers.Container collected = default!;
            EntityUid floorStack = default;
            EntityUid blocker = default;
            EntityUid[] paidOutputs = default!;
            int[] paidCounts = default!;
            var playerCoords = SEntMan.GetCoordinates(PlayerCoords);

            await Server.WaitPost(() =>
            {
                var carrier = SEntMan.SpawnEntity(null, playerCoords);
                collected = containers.EnsureContainer<Robust.Shared.Containers.Container>(carrier, "cargo");
                floorStack = Stack.SpawnAtPosition(3, "BasicMaterials", playerCoords);
                blocker = SEntMan.SpawnEntity("Screwdriver", playerCoords);
                for (var job = 0; job < 7; job++)
                {
                    var input = Stack.SpawnAtPosition(5, "FrontlineRawIron", playerCoords);
                    Assert.That(system.TryInsertInput(refinery, input), Is.True);
                    Assert.That(system.TrySubmitContainedJob(refinery, "FrontlineSteel"), Is.True);
                    Assert.That(SComp<StackComponent>(input).Count, Is.Zero);
                }
                Assert.That(system.GetJobs(refinery), Has.Count.EqualTo(7));
            });
            await Pair.RunSeconds(35.5f);
            await Server.WaitPost(() =>
            {
                Assert.That(system.GetJobs(refinery), Is.Empty);
                Assert.That(containers.TryGetContainer(refinery, "outputContainer", out output), Is.True);
                paidOutputs = output.ContainedEntities.ToArray();
                paidCounts = paidOutputs.Select(uid => SComp<StackComponent>(uid).Count).ToArray();
                Assert.That(paidCounts.Sum(), Is.EqualTo(35));
                Assert.That(system.BuildUiState(refinery).Outputs.Single().Amount, Is.EqualTo(35));
                Assert.That(HandSys.GetActiveItem(SPlayer), Is.Null);
                AssertConserved();

                Assert.That(HandSys.TryPickupAnyHand(SPlayer, blocker), Is.True);
                Assert.That(system.TryTakePlayerOutput(refinery, SPlayer), Is.False);
                Assert.That(HandSys.GetActiveItem(SPlayer), Is.EqualTo(blocker));
                Assert.That(output.ContainedEntities, Is.EqualTo(paidOutputs));
                Assert.That(paidOutputs.Select(uid => SComp<StackComponent>(uid).Count), Is.EqualTo(paidCounts));
                AssertConserved();
                Assert.That(HandSys.TryDropIntoContainer(SPlayer, blocker, collected), Is.True);

                Transform.SetCoordinates(SPlayer, playerCoords.Offset(new Vector2i(10, 0)));
                Assert.That(HandSys.GetActiveItem(SPlayer), Is.Null);
                Assert.That(system.TryTakePlayerOutput(refinery, SPlayer), Is.False);
                Assert.That(HandSys.GetActiveItem(SPlayer), Is.Null);
                Assert.That(output.ContainedEntities, Is.EqualTo(paidOutputs));
                Assert.That(paidOutputs.Select(uid => SComp<StackComponent>(uid).Count), Is.EqualTo(paidCounts));
                AssertConserved();
                Transform.SetCoordinates(SPlayer, playerCoords);

                foreach (var expected in paidOutputs)
                {
                    Assert.That(HandSys.GetActiveItem(SPlayer), Is.Null);
                    Assert.That(system.TryTakePlayerOutput(refinery, SPlayer), Is.True);
                    Assert.That(HandSys.GetActiveItem(SPlayer), Is.EqualTo(expected));
                    Assert.That(output.Contains(expected), Is.False);
                    AssertConserved();
                    Assert.That(HandSys.TryDropIntoContainer(SPlayer, expected, collected), Is.True);
                    Assert.That(collected.Contains(expected), Is.True);
                    Assert.That(HandSys.GetActiveItem(SPlayer), Is.Null);
                    AssertConserved();
                }
                Assert.That(system.TryTakePlayerOutput(refinery, SPlayer), Is.False);
            });
            await Server.WaitAssertion(() =>
            {
                Assert.That(output.ContainedEntities, Is.Empty);
                Assert.That(system.BuildUiState(refinery).Outputs, Is.Empty);
                Assert.That(collected.ContainedEntities.Where(uid => uid != blocker), Is.EquivalentTo(paidOutputs));
                Assert.That(paidOutputs.Select(uid => SComp<StackComponent>(uid).Count), Is.EqualTo(paidCounts));
                AssertConserved();
            });

            void AssertConserved()
            {
                var retained = output.ContainedEntities.Sum(uid => SComp<StackComponent>(uid).Count);
                var stored = collected.ContainedEntities.Where(uid => uid != blocker)
                    .Sum(uid => SComp<StackComponent>(uid).Count);
                var held = HandSys.GetActiveItem(SPlayer);
                var heldCount = held is { } item && item != blocker ? SComp<StackComponent>(item).Count : 0;
                Assert.That(heldCount + retained + stored, Is.EqualTo(35));
                Assert.That(system.BuildUiState(refinery).Outputs.Sum(entry => entry.Amount), Is.EqualTo(retained));
                Assert.That(SEntMan.EntityExists(floorStack), Is.True);
                Assert.That(SComp<StackComponent>(floorStack).Count, Is.EqualTo(3));
                Assert.That(containers.IsEntityInContainer(floorStack), Is.False);
                var query = SEntMan.EntityQueryEnumerator<StackComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out var stack, out var transform))
                {
                    if (stack.StackTypeId != "BasicMaterials" || transform.MapID != MapId)
                        continue;
                    if (uid != floorStack)
                        Assert.That(containers.IsEntityInContainer(uid), Is.True, "Production must not spill onto the floor.");
                }
                foreach (var uid in paidOutputs)
                {
                    Assert.That(SEntMan.EntityExists(uid), Is.True);
                    Assert.That(SComp<StackComponent>(uid).StackTypeId, Is.EqualTo("BasicMaterials"));
                }
            }
        }

        [Test]
        public async Task ConnectedPlayerTakesCompletedMaterialStackThroughNativeButtonWithoutDuplication()
        {
            await SpawnTarget("FrontlineRefinery");
            var refinery = STarget!.Value;
            var system = Server.System<FrontlineRefinerySystem>();
            var containers = Server.System<SharedContainerSystem>();
            BaseContainer output = default!;
            EntityUid materials = default;
            await Server.WaitPost(() =>
            {
                var input = Stack.SpawnAtPosition(5, "FrontlineRawIron", SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(system.TryInsertInput(refinery, input), Is.True);
                Assert.That(system.TrySubmitContainedJob(refinery, "FrontlineSteel"), Is.True);
            });
            await Pair.RunSeconds(5.1f);
            await Server.WaitPost(() =>
            {
                Assert.That(system.GetJobs(refinery), Is.Empty);
                Assert.That(containers.TryGetContainer(refinery, "outputContainer", out output), Is.True);
                Assert.That(output.ContainedEntities, Has.Count.EqualTo(1));
                materials = output.ContainedEntities.Single();
                Assert.That(SComp<StackComponent>(materials).StackTypeId, Is.EqualTo("BasicMaterials"));
                Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(5));
                Assert.That(HandSys.GetActiveItem(SPlayer), Is.Null);
            });
            await Interact();
            await Pair.RunUntilSynced();
            Assert.That(TryGetBui(FrontlineRefineryUiKey.Key, out _), Is.True);
            for (var click = 0; click < 2; click++)
            {
                Button take = default!;
                await Client.WaitAssertion(() =>
                {
                    var window = GetWindow<FrontlineRefineryWindow>();
                    Assert.That(TryGetControlFromChildren<Button>(button => button.Name == "TakeOutput", window, out take),
                        Is.True, "Refinery production BUI must expose a button named TakeOutput.");
                    if (click == 0)
                        Assert.That(take.Disabled, Is.False);
                });
                await ClickControl(take);
                await Pair.RunUntilSynced();
                await Server.WaitAssertion(() =>
                {
                    Assert.That(HandSys.GetActiveItem(SPlayer), Is.EqualTo(materials));
                    Assert.That(output.ContainedEntities, Is.Empty);
                    Assert.That(SEntMan.EntityExists(materials), Is.True);
                    Assert.That(SComp<StackComponent>(materials).StackTypeId, Is.EqualTo("BasicMaterials"));
                    Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(5));
                    var stacks = SEntMan.EntityQuery<StackComponent, TransformComponent>()
                        .Where(entity => entity.Item1.StackTypeId == "BasicMaterials" && entity.Item2.MapID == MapId)
                        .ToArray();
                    Assert.That(stacks, Has.Length.EqualTo(1));
                    Assert.That(stacks.Sum(entity => entity.Item1.Count), Is.EqualTo(5));
                });
            }
            await CloseBui(FrontlineRefineryUiKey.Key);
        }
    }

    private int CountStacks(string stackType)
    {
        return SEntMan.EntityQuery<StackComponent>()
            .Where(stack => stack.StackTypeId == stackType)
            .Sum(stack => stack.Count);
    }
}
