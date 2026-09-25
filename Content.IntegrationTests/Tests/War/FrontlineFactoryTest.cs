using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Stack;
using Content.Server.Storage.EntitySystems;
using Content.Server.War;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Storage.Components;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Content.Shared.War;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
[TestOf(typeof(FrontlineFactorySystem))]
public sealed class FrontlineFactoryTest : GameTest
{

    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: TestPersistedFrontlineFactory
          components:
          - type: FrontlineFactory
            jobs:
            - recipe: FrontlineFactoryBrutepack
              remaining: 1

        - type: entity
          id: TestTwoSlotFrontlineFactory
          components:
          - type: FrontlineFactory
            processingSlots: 2

        - type: frontlineFactoryRecipe
          id: TestInvalidFrontlineFactoryRecipe
          input:
            Steel: 1
          output: Brutepack1
          outputAmount: 0
          duration: 1

        - type: entity
          id: TestAbstractFrontlineFactoryOutput
          abstract: true

        - type: frontlineFactoryRecipe
          id: TestAbstractOutputFrontlineFactoryRecipe
          name: frontline-factory-recipe-unknown
          input:
            Steel: 1
          output: TestAbstractFrontlineFactoryOutput
          outputAmount: 1
          duration: 1

        """;

    [Test]
    public void ProductionRecipesAreValidAndLocalized()
    {
        var localization = Pair.Server.ResolveDependency<ILocalizationManager>();
        var recipes = Pair.Server.System<FrontlineFactorySystem>().GetAvailableRecipes().ToArray();

        Assert.That(recipes, Has.Length.EqualTo(4));
        Assert.That(recipes, Has.All.Matches<FrontlineFactoryRecipePrototype>(recipe =>
            recipe.Input.Count > 0 &&
            recipe.Input.Values.All(amount => amount > 0) &&
            recipe.OutputAmount > 0 &&
            recipe.Duration > TimeSpan.Zero &&
            localization.HasString(recipe.Name)));
        Assert.That(recipes, Has.All.Matches<FrontlineFactoryRecipePrototype>(recipe =>
            recipe.Input.Keys.Single().Id == "BasicMaterials"));
        Assert.That(recipes.Single(recipe => recipe.ID == "FrontlineFactoryMk58").Input.Values.Single(), Is.EqualTo(20));
        Assert.That(recipes.Single(recipe => recipe.ID == "FrontlineFactoryMagazinePistol").Input.Values.Single(), Is.EqualTo(5));
        Assert.That(recipes.Single(recipe => recipe.ID == "FrontlineFactoryBrutepack").Input.Values.Single(), Is.EqualTo(5));
        Assert.That(recipes.Single(recipe => recipe.ID == "FrontlineFactorySupplyCrate").Input.Values.Single(), Is.EqualTo(10));
    }

    [Test]
    public void InvalidOutputAndMissingLocalizationAreRejected()
    {
        var system = Pair.Server.System<FrontlineFactorySystem>();
        var recipes = system.GetAvailableRecipes().Select(recipe => recipe.ID);

        Assert.That(recipes, Does.Not.Contain("TestAbstractOutputFrontlineFactoryRecipe"));
        Assert.That(system.HasValidLocalization("test-frontline-factory-missing-localization"), Is.False);
    }

    [Test]
    public async Task PlayerInsertionRequiresRangeAndKeepsPhysicalEntity()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var hands = server.System<SharedHandsSystem>();
        EntityUid factory = default;
        EntityUid input = default;
        bool remote = true;
        bool nearby = false;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            input = stacks.SpawnAtPosition(10, "BasicMaterials", map.GridCoords);
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2i(10, 0)));
            Assert.That(hands.TryPickupAnyHand(user, input), Is.True);
            remote = system.TryInsertPlayerInput(factory, user, input);
            SEntMan.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords);
            nearby = system.TryInsertPlayerInput(factory, user, input);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(remote, Is.False);
            Assert.That(nearby, Is.True);
            Assert.That(SEntMan.EntityExists(input), Is.True);
            Assert.That(SComp<FrontlineFactoryComponent>(factory).InputContainer.Contains(input), Is.True);
        });
    }

    [Test]
    public async Task SubmissionConsumesOnlyContainedInput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        EntityUid factory = default;
        EntityUid materials = default;
        EntityUid outside = default;
        var outsideBefore = 0;
        bool accepted = false;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            materials = stacks.SpawnAtPosition(20, "BasicMaterials", map.GridCoords);
            outside = stacks.SpawnAtPosition(100, "BasicMaterials", map.GridCoords);
            outsideBefore = SComp<StackComponent>(outside).Count;
            Assert.That(system.TryInsertInput(factory, materials), Is.True);
            accepted = system.TrySubmitContainedJob(factory, "FrontlineFactoryMk58");
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.Zero);
            Assert.That(SComp<StackComponent>(outside).Count, Is.EqualTo(outsideBefore));
            Assert.That(system.GetJobs(factory), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task RejectedSubmissionAndRemoteActionsPreserveInputs()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        EntityUid factory = default;
        EntityUid materials = default;
        EntityUid outside = default;
        EntityUid rawIron = default;
        EntityUid technologyAlloy = default;
        EntityUid user = default;
        bool remoteSubmit = true;
        bool remoteEject = true;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            materials = stacks.SpawnAtPosition(1, "BasicMaterials", map.GridCoords);
            outside = stacks.SpawnAtPosition(10, "BasicMaterials", map.GridCoords);
            var vanillaSteel = stacks.SpawnAtPosition(5, "Steel", map.GridCoords);
            rawIron = stacks.SpawnAtPosition(5, "FrontlineRawIron", map.GridCoords);
            technologyAlloy = stacks.SpawnAtPosition(1, "TechnologyAlloy", map.GridCoords);
            user = SEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2i(10, 0)));
            Assert.That(system.TryInsertInput(factory, materials), Is.True);
            Assert.That(system.TryInsertInput(factory, vanillaSteel), Is.False);
            Assert.That(system.TryInsertInput(factory, rawIron), Is.False);
            Assert.That(system.TryInsertInput(factory, technologyAlloy), Is.False);
            Assert.That(system.TrySubmitContainedJob(factory, "MissingFrontlineFactoryRecipe"), Is.False);
            Assert.That(system.TrySubmitContainedJob(factory, "TestInvalidFrontlineFactoryRecipe"), Is.False);
            Assert.That(system.TrySubmitContainedJob(factory, "FrontlineFactoryBrutepack"), Is.False);
            remoteSubmit = system.TrySubmitPlayerJob(factory, user, "FrontlineFactoryBrutepack");
            remoteEject = system.TryEjectPlayerInputs(factory, user);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(remoteSubmit, Is.False);
            Assert.That(remoteEject, Is.False);
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(1));
            Assert.That(SComp<StackComponent>(outside).Count, Is.EqualTo(10));
            Assert.That(SComp<FrontlineFactoryComponent>(factory).InputContainer.Contains(materials), Is.True);
            Assert.That(system.GetJobs(factory), Is.Empty);
        });

        await server.WaitPost(() =>
        {
            system.EjectInputs(factory);
            Assert.That(SEntMan.EntityExists(materials), Is.True);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(SComp<FrontlineFactoryComponent>(factory).InputContainer.ContainedEntities, Is.Empty);
            Assert.That(SEntMan.EntityExists(materials), Is.True);
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task OutputAmountSpawnsSeparateOrdinaryEntitiesAfterDelay()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();

        await server.WaitPost(() =>
        {
            var factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(5, "BasicMaterials", map.GridCoords);
            Assert.That(system.TrySubmitJob(factory, "FrontlineFactoryBrutepack", new[] { materials }), Is.True);
        });
        await Pair.RunSeconds(4.9f);
        await server.WaitAssertion(() => Assert.That(CountPrototype("Brutepack1"), Is.Zero));
        await Pair.RunSeconds(0.2f);
        await server.WaitAssertion(() => Assert.That(CountPrototype("Brutepack1"), Is.EqualTo(2)));
    }

    [TestCase("FrontlineFactoryMk58", 20, 10f, "FrontlineWeaponPistolMk58")]
    [TestCase("FrontlineFactoryMagazinePistol", 5, 5f, "MagazinePistol")]
    [TestCase("FrontlineFactorySupplyCrate", 10, 5f, "FrontlineSupplyCrate")]
    public async Task ProductionJobsSpawnExpectedPhysicalOutput(
        string recipe,
        int cost,
        float duration,
        string output)
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var before = 0;

        await server.WaitPost(() =>
        {
            before = CountPrototype(output);
            var factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(cost, "BasicMaterials", map.GridCoords);
            Assert.That(system.TrySubmitJob(factory, recipe, new[] { materials }), Is.True);
        });
        await Pair.RunSeconds(duration + 0.1f);
        await server.WaitAssertion(() => Assert.That(CountPrototype(output), Is.EqualTo(before + 1)));
    }

    [Test]
    public async Task FactoryMk58IsUnloadedAndAcceptsProducedLoadedMagazine()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var factorySystem = server.System<FrontlineFactorySystem>();
        var stackSystem = server.System<StackSystem>();
        var slotSystem = server.System<ItemSlotsSystem>();
        var gunSystem = server.System<SharedGunSystem>();
        EntityUid gun = default;
        EntityUid magazine = default;

        await server.WaitPost(() =>
        {
            var factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stackSystem.SpawnAtPosition(25, "BasicMaterials", map.GridCoords);
            Assert.That(factorySystem.TrySubmitJob(factory, "FrontlineFactoryMk58", new[] { materials }), Is.True);
            Assert.That(factorySystem.TrySubmitJob(factory, "FrontlineFactoryMagazinePistol", new[] { materials }), Is.True);
        });
        await Pair.RunSeconds(15.2f);
        await server.WaitAssertion(() =>
        {
            gun = FindPrototype("FrontlineWeaponPistolMk58");
            magazine = FindPrototype("MagazinePistol");
            Assert.That(gun, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(magazine, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(gunSystem.GetAmmoCount(gun), Is.Zero);
            Assert.That(gunSystem.GetAmmoCount(magazine), Is.EqualTo(10));
        });
        await server.WaitPost(() => Assert.That(slotSystem.TryInsert(gun, "gun_magazine", magazine, null), Is.True));
        await server.WaitAssertion(() => Assert.That(gunSystem.GetAmmoCount(gun), Is.EqualTo(10)));
    }

    [Test]
    public async Task DefaultProcessingSlotKeepsSecondJobWaiting()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        EntityUid factory = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(10, "BasicMaterials", map.GridCoords);
            Assert.That(system.TrySubmitJob(factory, "FrontlineFactoryBrutepack", new[] { materials }), Is.True);
            Assert.That(system.TrySubmitJob(factory, "FrontlineFactoryBrutepack", new[] { materials }), Is.True);
        });
        await Pair.RunSeconds(1f);
        await server.WaitAssertion(() =>
        {
            var jobs = system.GetJobs(factory);
            Assert.That(jobs[0].Remaining.TotalSeconds, Is.EqualTo(4).Within(0.2));
            Assert.That(jobs[1].Remaining, Is.EqualTo(TimeSpan.FromSeconds(5)));
        });
    }

    [Test]
    public async Task FifoSlotsPausedAndPersistedJobsBehave()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var mapSystem = server.System<SharedMapSystem>();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        EntityUid factory = default;
        EntityUid persisted = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("TestTwoSlotFrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(15, "BasicMaterials", map.GridCoords);
            for (var i = 0; i < 3; i++)
                Assert.That(system.TrySubmitJob(factory, "FrontlineFactoryBrutepack", new[] { materials }), Is.True);
            persisted = SEntMan.SpawnEntity("TestPersistedFrontlineFactory", map.GridCoords);
            SComp<FrontlineFactoryComponent>(persisted).Jobs.Insert(0, new FrontlineFactoryJob
            {
                Recipe = "MissingFrontlineFactoryRecipe",
                Remaining = TimeSpan.Zero,
            });
            mapSystem.SetPaused(map.MapId, true);
        });
        await Pair.RunSeconds(1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory).Select(job => job.Remaining),
                Is.EqualTo(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }));
            Assert.That(system.GetJobs(persisted), Has.Count.EqualTo(2));
        });
        await server.WaitPost(() => mapSystem.SetPaused(map.MapId, false));
        await Pair.RunSeconds(0.6f);
        await server.WaitAssertion(() =>
        {
            var jobs = system.GetJobs(factory);
            Assert.That(jobs[0].Remaining.TotalSeconds, Is.EqualTo(4.4).Within(0.2));
            Assert.That(jobs[1].Remaining.TotalSeconds, Is.EqualTo(4.4).Within(0.2));
            Assert.That(jobs[2].Remaining, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(system.GetJobs(persisted), Has.Count.EqualTo(1));
        });
        await Pair.RunSeconds(0.5f);
        await server.WaitAssertion(() => Assert.That(system.GetJobs(persisted), Is.Empty));
    }

    [Test]
    public async Task ReentrantInputMutationRollsBackConsumedBasicMaterials()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var mutation = server.System<FrontlineRefineryTest.ReentrantStackMutationSystem>();
        EntityUid factory = default;
        bool accepted = true;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var first = stacks.SpawnAtPosition(3, "BasicMaterials", map.GridCoords);
            var second = stacks.SpawnAtPosition(2, "BasicMaterials", map.GridCoords);
            mutation.Target = second;
            mutation.Enabled = true;
            accepted = system.TrySubmitJob(factory, "FrontlineFactoryBrutepack", new[] { first, second });
        });
        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(3));
            Assert.That(system.GetJobs(factory), Is.Empty);
        });
    }

    [Test]
    public async Task FinalInputEventTerminatingFactoryRollsBackBasicMaterials()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var mutation = server.System<FrontlineRefineryTest.ReentrantStackMutationSystem>();
        bool accepted = true;

        await server.WaitPost(() =>
        {
            var factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(5, "BasicMaterials", map.GridCoords);
            mutation.Target = factory;
            mutation.Enabled = true;
            accepted = system.TrySubmitJob(factory, "FrontlineFactoryBrutepack", new[] { materials });
        });
        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task ReentrantCurrentStackRestoreRejectsWithoutDuplication()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var mutation = server.System<FrontlineRefineryTest.ReentrantStackMutationSystem>();
        EntityUid factory = default;
        bool accepted = true;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(5, "BasicMaterials", map.GridCoords);
            mutation.RestoreCurrent = true;
            mutation.Enabled = true;
            accepted = system.TrySubmitJob(factory, "FrontlineFactoryBrutepack", new[] { materials });
        });
        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(CountStacks("BasicMaterials"), Is.EqualTo(5));
            Assert.That(system.GetJobs(factory), Is.Empty);
        });
    }

    [Test]
    public async Task UiStateUsesLocalizedNamesAndProductionEntityHasBui()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        EntityUid factory = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(5, "BasicMaterials", map.GridCoords);
            Assert.That(system.TryInsertInput(factory, materials), Is.True);
            Assert.That(system.TrySubmitContainedJob(factory, "FrontlineFactoryBrutepack"), Is.True);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<ActivatableUIComponent>(factory), Is.True);
            Assert.That(SEntMan.HasComponent<UserInterfaceComponent>(factory), Is.True);
            var state = system.BuildUiState(factory);
            Assert.That(state.Jobs.Single().Recipe.Id, Is.EqualTo("FrontlineFactoryBrutepack"));
            Assert.That(state.Recipes.Single(recipe => recipe.Id == "FrontlineFactoryBrutepack").Output,
                Is.EqualTo(new Robust.Shared.Prototypes.EntProtoId("Brutepack1")));
        });
    }

    [Test]
    public async Task SupplyCrateStoresAndReturnsPhysicalSupplies()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var storage = server.System<EntityStorageSystem>();
        EntityUid crate = default;
        EntityUid item = default;

        await server.WaitPost(() =>
        {
            crate = SEntMan.SpawnEntity("FrontlineSupplyCrate", map.GridCoords);
            item = SEntMan.SpawnEntity("WeaponPistolMk58", map.GridCoords);
            Assert.That(storage.Insert(item, crate), Is.True);
        });
        await server.WaitAssertion(() =>
        {
            var contents = SComp<EntityStorageComponent>(crate).Contents.ContainedEntities;
            Assert.That(contents, Is.EqualTo(new[] { item }));
            Assert.That(SEntMan.EntityExists(item), Is.True);
        });
        await server.WaitPost(() => Assert.That(storage.Remove(item, crate), Is.True));
        await server.WaitAssertion(() =>
        {
            Assert.That(SComp<EntityStorageComponent>(crate).Contents.ContainedEntities, Is.Empty);
            Assert.That(SEntMan.EntityExists(item), Is.True);
        });
    }

    private int CountPrototype(string id) => SEntMan.EntityQuery<MetaDataComponent>()
        .Count(meta => meta.EntityPrototype?.ID == id);

    private EntityUid FindPrototype(string id)
    {
        var query = SEntMan.EntityQueryEnumerator<MetaDataComponent>();
        while (query.MoveNext(out var uid, out var metadata))
        {
            if (metadata.EntityPrototype?.ID == id)
                return uid;
        }

        return EntityUid.Invalid;
    }

    private static int SumContained(EntityUid factory, string stackType, IEntityManager entMan) =>
        entMan.GetComponent<FrontlineFactoryComponent>(factory).InputContainer.ContainedEntities
            .Select(uid => entMan.TryGetComponent(uid, out StackComponent stack) && stack.StackTypeId == stackType
                ? stack.Count
                : 0)
            .Sum();

    private int CountStacks(string stackType) => SEntMan.EntityQuery<StackComponent>()
        .Where(stack => stack.StackTypeId == stackType)
        .Sum(stack => stack.Count);
}
