using System.Linq;
using Content.Client.War;
using Content.IntegrationTests.Tests.Interaction;
using Robust.Client.UserInterface.Controls;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Stack;
using Content.Server.War;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Item;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Storage.Components;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Content.Shared.War;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
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

        - type: frontlineFactoryRecipe
          id: TestOrdinaryTwoItemFrontlineFactoryRecipe
          name: frontline-factory-recipe-unknown
          input:
            BasicMaterials: 5
          output: Brutepack1
          outputAmount: 2
          duration: 5

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
        var recipes = Pair.Server.System<FrontlineFactorySystem>().GetAvailableRecipes()
            .Where(recipe => recipe.ID.StartsWith("FrontlineFactory", StringComparison.Ordinal)).ToArray();

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
        EntityUid factory = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(5, "BasicMaterials", map.GridCoords);
            Assert.That(system.TrySubmitJob(factory, "TestOrdinaryTwoItemFrontlineFactoryRecipe", new[] { materials }), Is.True);
        });
        await Pair.RunSeconds(4.9f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountPrototype("Brutepack1"), Is.Zero);
            Assert.That(SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities, Is.Empty);
        });
        await Pair.RunSeconds(0.2f);
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory), Is.Empty);
            Assert.That(CountPrototype("Brutepack1"), Is.EqualTo(2));
            var output = SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities;
            Assert.That(output.Distinct().Count(), Is.EqualTo(2));
            Assert.That(output, Has.Count.EqualTo(2));
            Assert.That(output.All(uid => SComp<MetaDataComponent>(uid).EntityPrototype?.ID == "Brutepack1" &&
                !SEntMan.HasComponent<FrontlineSupplyCrateComponent>(uid)), Is.True);
        });
    }

    [Test]
    public async Task RollbackQueuesAllStagedOutputsBeforeNativeRemovalForPaidRetry()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var mutation = server.System<ObserveRollbackOrderingSystem>();
        EntityUid factory = default;
        EntityUid materials = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            materials = stacks.SpawnAtPosition(10, "BasicMaterials", map.GridCoords);
            Assert.That(system.TryInsertInput(factory, materials), Is.True);
            Assert.That(system.TrySubmitContainedJob(factory, "TestOrdinaryTwoItemFrontlineFactoryRecipe"), Is.True);
            SComp<FrontlineFactoryComponent>(factory).Jobs.Single().Remaining = TimeSpan.Zero;
            mutation.FactoryUid = factory;
            mutation.First = default;
            mutation.Second = default;
            mutation.Rejected = false;
            mutation.RemovalObserved = false;
            mutation.SecondWasQueuedBeforeFirstRemoval = false;
            mutation.Enabled = true;
        });
        // Observe the first completion tick before the retained job can retry.
        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(mutation.Rejected, Is.True, "The second native insertion must invalidate retention.");
            Assert.That(mutation.RemovalObserved, Is.True, "Rollback must reach the first output's native removal callback.");
            Assert.That(mutation.SecondWasQueuedBeforeFirstRemoval, Is.True,
                "Rollback must queue the second staged output before the first output's native removal.");
            Assert.That(mutation.First, Is.Not.EqualTo(default(EntityUid)));
            Assert.That(mutation.Second, Is.Not.EqualTo(default(EntityUid)));
            Assert.That(mutation.Second, Is.Not.EqualTo(mutation.First));
            foreach (var output in new[] { mutation.First, mutation.Second })
            {
                Assert.That(!SEntMan.EntityExists(output) || SEntMan.IsQueuedForDeletion(output) ||
                    SComp<MetaDataComponent>(output).EntityLifeStage >= EntityLifeStage.Terminating, Is.True,
                    "Rollback must not leave a staged output available for duplication.");
            }
            Assert.That(system.GetJobs(factory).Single().Recipe.Id, Is.EqualTo("TestOrdinaryTwoItemFrontlineFactoryRecipe"));
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(5));
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.EqualTo(5));
        });
        await server.WaitPost(() => mutation.Enabled = false);
        // Drain queued deletion and retry, then prove another tick cannot duplicate output.
        await Pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(mutation.First), Is.False);
            Assert.That(SEntMan.EntityExists(mutation.Second), Is.False);
            Assert.That(system.GetJobs(factory), Is.Empty);
            var output = SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities;
            Assert.That(output, Has.Count.EqualTo(2));
            Assert.That(output.Distinct().Count(), Is.EqualTo(2));
            Assert.That(output.All(uid => uid != mutation.First && uid != mutation.Second &&
                SComp<MetaDataComponent>(uid).EntityPrototype?.ID == "Brutepack1" &&
                SComp<MetaDataComponent>(uid).EntityLifeStage < EntityLifeStage.Terminating &&
                !SEntMan.IsQueuedForDeletion(uid)), Is.True);
            var available = 0;
            var query = SEntMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var metadata, out var transform))
            {
                if (transform.MapID == map.MapId && metadata.EntityPrototype?.ID == "Brutepack1" &&
                    metadata.EntityLifeStage < EntityLifeStage.Terminating && !SEntMan.IsQueuedForDeletion(uid))
                    available++;
            }
            Assert.That(available, Is.EqualTo(2));
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(5));
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.EqualTo(5));
        });
    }

    public sealed class ObserveRollbackOrderingSystem : EntitySystem
    {
        public EntityUid FactoryUid;
        public EntityUid First;
        public EntityUid Second;
        public bool Enabled;
        public bool Rejected;
        public bool RemovalObserved;
        public bool SecondWasQueuedBeforeFirstRemoval;

        public override void Initialize()
        {
            SubscribeLocalEvent<ContainerManagerComponent, EntInsertedIntoContainerMessage>(OnInserted);
            SubscribeLocalEvent<ContainerManagerComponent, EntRemovedFromContainerMessage>(OnRemoved);
        }

        private void OnInserted(Entity<ContainerManagerComponent> factory, ref EntInsertedIntoContainerMessage args)
        {
            if (!Enabled || factory.Owner != FactoryUid || Rejected ||
                args.Container.ID != FrontlineFactoryComponent.OutputContainerId)
                return;

            if (First == default)
            {
                First = args.Entity;
                return;
            }

            Second = args.Entity;
            QueueDel(First);
            Rejected = true;
        }

        private void OnRemoved(Entity<ContainerManagerComponent> factory, ref EntRemovedFromContainerMessage args)
        {
            if (!Enabled || factory.Owner != FactoryUid || !Rejected || RemovalObserved || args.Entity != First ||
                args.Container.ID != FrontlineFactoryComponent.OutputContainerId)
                return;

            RemovalObserved = true;
            SecondWasQueuedBeforeFirstRemoval = EntityManager.IsQueuedForDeletion(Second);
        }
    }

    [TestCase("FrontlineFactoryMk58", 20, 10f, "FrontlineFactoryWeaponCrate", "FrontlineWeaponPistolMk58", 1)]
    [TestCase("FrontlineFactoryMagazinePistol", 5, 5f, "FrontlineFactoryAmmoCrate", "MagazinePistol", 1)]
    [TestCase("FrontlineFactorySupplyCrate", 10, 5f, "FrontlineSupplyCrate", "SoldierSupplies", 5)]
    public async Task ProductionJobsSpawnExpectedPhysicalOutput(
        string recipe,
        int cost,
        float duration,
        string output,
        string product,
        int amount)
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var before = 0;
        EntityUid factory = default;

        await server.WaitPost(() =>
        {
            before = CountPrototype(output);
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(cost, "BasicMaterials", map.GridCoords);
            Assert.That(system.TrySubmitJob(factory, recipe, new[] { materials }), Is.True);
        });
        await Pair.RunSeconds(duration + 0.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(CountPrototype(output), Is.EqualTo(before + 1));
            Assert.That(system.GetJobs(factory), Is.Empty);
            var retained = SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities;
            Assert.That(retained, Has.Count.EqualTo(1));
            var crate = retained.Single();
            Assert.That(SEntMan.EntityExists(crate), Is.True);
            Assert.That(SComp<MetaDataComponent>(crate).EntityPrototype?.ID, Is.EqualTo(output));
            Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Product.Id, Is.EqualTo(product));
            Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Amount, Is.EqualTo(amount));
        });
    }

    [TestCase("FrontlineFactoryMk58", 20, 10f, "FrontlineWeaponPistolMk58", 1, false)]
    [TestCase("FrontlineFactoryMagazinePistol", 5, 5f, "MagazinePistol", 1, false)]
    [TestCase("FrontlineFactoryBrutepack", 5, 5f, "Brutepack1", 2, false)]
    [TestCase("FrontlineFactoryMk58", 20, 10f, "FrontlineWeaponPistolMk58", 1, true)]
    [TestCase("FrontlineFactoryMagazinePistol", 5, 5f, "MagazinePistol", 1, true)]
    [TestCase("FrontlineFactoryBrutepack", 5, 5f, "Brutepack1", 2, true)]
    public async Task ProductionJobsRetainOneSealedCrateWithExactStockpileEntitlement(
        string recipe,
        int cost,
        float duration,
        string product,
        int amount,
        bool alreadyFunded)
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var stockpiles = server.System<FrontlineStockpileSystem>();
        var hands = server.System<SharedHandsSystem>();
        EntityUid factory = default;
        EntityUid core = default;
        EntityUid actor = default;
        EntityUid crate = default;
        var remaining = alreadyFunded ? 1f : duration;
        var productEntity = server.ProtoMan.Index(new Robust.Shared.Prototypes.ProtoId<FrontlineSupplyProductPrototype>(product)).Entity?.Id;

        await server.WaitPost(() =>
        {
            core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            factory = SEntMan.SpawnEntity(alreadyFunded ? "TestPersistedFrontlineFactory" : "FrontlineFactory",
                map.GridCoords);
            if (alreadyFunded)
            {
                // Load-shaped state: MapInit activates the existing serialized queue;
                // retain its paid recipe identity and partial progress without resubmission.
                var job = SComp<FrontlineFactoryComponent>(factory).Jobs.Single();
                job.Recipe = recipe;
                job.Remaining = TimeSpan.FromSeconds(remaining);
            }
            else
            {
                var materials = stacks.SpawnAtPosition(cost, "BasicMaterials", map.GridCoords);
                Assert.That(system.TryInsertInput(factory, materials), Is.True);
                Assert.That(system.TrySubmitContainedJob(factory, recipe), Is.True);
            }

            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.Zero);
            var queued = system.GetJobs(factory).Single();
            Assert.That(queued.Recipe.Id, Is.EqualTo(recipe));
            Assert.That(queued.Remaining, Is.EqualTo(TimeSpan.FromSeconds(remaining)));
            Assert.That(SComp<FrontlineStockpileComponent>(core).Counts, Is.Empty);
        });
        await Pair.RunSeconds(remaining - 0.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory).Single().Recipe.Id, Is.EqualTo(recipe));
            Assert.That(SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities, Is.Empty);
        });
        await Pair.RunSeconds(0.2f);
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory), Is.Empty);
            var output = SComp<FrontlineFactoryComponent>(factory).OutputContainer;
            Assert.That(output.ContainedEntities, Has.Count.EqualTo(1),
                "Each paid recipe must retain exactly one sealed batch, not individual goods.");
            var retained = output.ContainedEntities.Single();
            Assert.That(SEntMan.EntityExists(retained), Is.True);
            Assert.That(SEntMan.HasComponent<FrontlineSupplyCrateComponent>(retained), Is.True,
                "Factory goods must remain virtual inside a sealed supply crate until stockpile withdrawal.");
            var contents = SComp<FrontlineSupplyCrateComponent>(retained);
            Assert.That(contents.Product.Id, Is.EqualTo(product));
            Assert.That(contents.Amount, Is.EqualTo(amount), "Packaging must not multiply the paid entitlement by five.");
            Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(retained), Is.False);
            Assert.That(SEntMan.HasComponent<EntityStorageComponent>(retained), Is.False);
            Assert.That(SEntMan.HasComponent<Content.Shared.Storage.StorageComponent>(retained), Is.False);
            Assert.That(SEntMan.EntityQuery<MetaDataComponent, TransformComponent>()
                .Count(entity => entity.Item2.MapID == map.MapId && entity.Item1.EntityPrototype?.ID == productEntity), Is.Zero,
                "Completion must not create loose product entities, including floor output.");
            Assert.That(SEntMan.EntityQuery<FrontlineSupplyCrateComponent, TransformComponent>()
                .Count(entity => entity.Item2.MapID == map.MapId), Is.EqualTo(1),
                "The sole crate must be retained in factory output, with no additional floor crate.");
        });
        await server.WaitPost(() =>
        {
            crate = SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities.Single();
            Assert.That(system.TryTakePlayerOutput(factory, actor), Is.True);
            Assert.That(hands.GetActiveItem(actor), Is.EqualTo(crate));
            Assert.That(stockpiles.TrySubmitHeld(core, actor), Is.True);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(crate), Is.False);
            Assert.That(SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities, Is.Empty);
            var counts = SComp<FrontlineStockpileComponent>(core).Counts;
            Assert.That(counts, Has.Count.EqualTo(1));
            Assert.That(counts[new Robust.Shared.Prototypes.ProtoId<FrontlineSupplyProductPrototype>(product)],
                Is.EqualTo(amount), "Stockpile credit must be exactly 1/1/2, not the generic five-unit crate default.");
            Assert.That(SEntMan.EntityQuery<MetaDataComponent, TransformComponent>()
                .Count(entity => entity.Item2.MapID == map.MapId && entity.Item1.EntityPrototype?.ID == productEntity), Is.Zero,
                "Depositing a sealed batch must not materialize its goods.");
        });
    }

    [Test]
    public async Task SupplyCrateCompletionRetainsSealedOutputWithoutFloorOutput()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var containers = server.System<SharedContainerSystem>();
        EntityUid factory = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            var materials = stacks.SpawnAtPosition(10, "BasicMaterials", map.GridCoords);
            Assert.That(system.TryInsertInput(factory, materials), Is.True);
            Assert.That(system.TrySubmitContainedJob(factory, "FrontlineFactorySupplyCrate"), Is.True);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.Zero);
            Assert.That(system.GetJobs(factory), Has.Count.EqualTo(1));
        });
        await Pair.RunSeconds(5.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory), Is.Empty);
            Assert.That(containers.TryGetContainer(factory, "outputContainer", out var output), Is.True,
                "Completed supply crate must be retained in factory outputContainer.");
            Assert.That(output!.ContainedEntities, Has.Count.EqualTo(1));
            var crate = output.ContainedEntities.Single();
            Assert.That(SComp<MetaDataComponent>(crate).EntityPrototype?.ID, Is.EqualTo("FrontlineSupplyCrate"));
            var contents = SComp<FrontlineSupplyCrateComponent>(crate);
            Assert.That(contents.Product.Id, Is.EqualTo("SoldierSupplies"));
            Assert.That(contents.Amount, Is.EqualTo(5));
            Assert.That(SEntMan.HasComponent<EntityStorageComponent>(crate), Is.False);
            Assert.That(SEntMan.HasComponent<Content.Shared.Storage.StorageComponent>(crate), Is.False);
            Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(crate), Is.False);

            var query = SEntMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var metadata, out var transform))
            {
                if (transform.MapID == map.MapId && metadata.EntityPrototype?.ID == "FrontlineSupplyCrate")
                    Assert.That(output.Contains(uid), Is.True, "Supply crate output must not be loose on the floor.");
            }
        });
    }

    [Test]
    public async Task QueuedSupplyCrateDeletionKeepsPaidJobForRetry()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var mutation = server.System<QueueSupplyCrateOnStartupSystem>();
        EntityUid factory = default;
        EntityUid materials = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            materials = stacks.SpawnAtPosition(15, "BasicMaterials", map.GridCoords);
            mutation.ProducedCrate = default;
            mutation.Fired = false;
            mutation.Enabled = true;
            Assert.That(system.TryInsertInput(factory, materials), Is.True);
            Assert.That(system.TrySubmitContainedJob(factory, "FrontlineFactorySupplyCrate"), Is.True);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory), Has.Count.EqualTo(1));
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(5));
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.EqualTo(5));
        });

        await Pair.RunSeconds(4.9f);
        // Stop on the first completion tick: queued deletion drains after systems update,
        // and the retained job cannot automatically retry until another tick is requested.
        for (var tick = 0; tick < 60 && !mutation.Fired; tick++)
            await Pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(mutation.Fired, Is.True, "The real produced crate must trigger ComponentStartup.");
            Assert.That(system.GetJobs(factory), Has.Count.EqualTo(1),
                "A queued-for-deletion output must not retire its already-paid factory job.");
            Assert.That(system.GetJobs(factory).Single().Recipe.Id, Is.EqualTo("FrontlineFactorySupplyCrate"));
            Assert.That(SEntMan.EntityExists(mutation.ProducedCrate), Is.False);
            Assert.That(SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities, Is.Empty);
            Assert.That(SEntMan.EntityQuery<MetaDataComponent, TransformComponent>()
                .Count(entity => entity.Item1.EntityPrototype?.ID == "FrontlineSupplyCrate" &&
                    entity.Item2.MapID == map.MapId), Is.Zero);
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(5));
        });

        await server.WaitPost(() => mutation.Enabled = false);
        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory), Is.Empty);
            var output = SComp<FrontlineFactoryComponent>(factory).OutputContainer;
            Assert.That(output.ContainedEntities, Has.Count.EqualTo(1));
            var crate = output.ContainedEntities.Single();
            Assert.That(crate, Is.Not.EqualTo(mutation.ProducedCrate));
            Assert.That(SEntMan.EntityExists(crate), Is.True);
            Assert.That(SComp<MetaDataComponent>(crate).EntityPrototype?.ID, Is.EqualTo("FrontlineSupplyCrate"));
            var contents = SComp<FrontlineSupplyCrateComponent>(crate);
            Assert.That(contents.Product.Id, Is.EqualTo("SoldierSupplies"));
            Assert.That(contents.Amount, Is.EqualTo(5));
            Assert.That(SEntMan.HasComponent<EntityStorageComponent>(crate), Is.False);
            Assert.That(SEntMan.HasComponent<Content.Shared.Storage.StorageComponent>(crate), Is.False);
            Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(crate), Is.False);
            Assert.That(SEntMan.EntityQuery<MetaDataComponent, TransformComponent>()
                .Count(entity => entity.Item1.EntityPrototype?.ID == "FrontlineSupplyCrate" &&
                    entity.Item2.MapID == map.MapId), Is.EqualTo(1));
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(5));
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task PlayerTakeRejectsOutputQueuedForDeletionByNativePickup()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineFactorySystem>();
        var stacks = server.System<StackSystem>();
        var hands = server.System<SharedHandsSystem>();
        var mutation = server.System<QueueFactoryOutputOnEquipSystem>();
        EntityUid factory = default;
        EntityUid actor = default;
        EntityUid materials = default;
        EntityUid crate = default;
        bool taken = true;
        bool queuedDuringTake = false;
        bool heldDuringTake = false;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            materials = stacks.SpawnAtPosition(15, "BasicMaterials", map.GridCoords);
            Assert.That(system.TryInsertInput(factory, materials), Is.True);
            Assert.That(system.TrySubmitContainedJob(factory, "FrontlineFactorySupplyCrate"), Is.True);
        });
        await Pair.RunSeconds(5.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(system.GetJobs(factory), Is.Empty);
            var output = SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities;
            Assert.That(output, Has.Count.EqualTo(1));
            var retained = output.Single();
            Assert.That(SComp<MetaDataComponent>(retained).EntityPrototype?.ID, Is.EqualTo("FrontlineSupplyCrate"));
            Assert.That(SComp<FrontlineSupplyCrateComponent>(retained).Product.Id, Is.EqualTo("SoldierSupplies"));
            Assert.That(SComp<FrontlineSupplyCrateComponent>(retained).Amount, Is.EqualTo(5));
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.EqualTo(5));
            Assert.That(hands.GetActiveItem(actor), Is.Null);
            Assert.That(SEntMan.EntityQuery<FrontlineSupplyCrateComponent, TransformComponent>()
                .Count(entity => entity.Item2.MapID == map.MapId), Is.EqualTo(1));
        });
        await server.WaitPost(() =>
        {
            crate = SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities.Single();
            mutation.Target = crate;
            mutation.Fired = false;
            taken = system.TryTakePlayerOutput(factory, actor);
            queuedDuringTake = SEntMan.IsQueuedForDeletion(crate);
            heldDuringTake = hands.GetActiveItem(actor) == crate;
        });
        // Drain the doomed hand entity before asserting RED so fixture cleanup stays stable.
        await Pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(mutation.Fired, Is.True, "The real hand insertion must trigger GotEquippedHandEvent.");
            Assert.That(queuedDuringTake, Is.True);
            Assert.That(heldDuringTake, Is.True, "The callback must queue the output after native hand insertion.");
            Assert.That(SEntMan.EntityExists(crate), Is.False);
            Assert.That(hands.GetActiveItem(actor), Is.Null);
            Assert.That(SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities, Is.Empty);
            Assert.That(system.GetJobs(factory), Is.Empty, "Taking output must not duplicate the completed paid job.");
            Assert.That(SumContained(factory, "BasicMaterials", SEntMan), Is.EqualTo(5));
            Assert.That(SEntMan.EntityQuery<FrontlineSupplyCrateComponent, TransformComponent>()
                .Count(entity => entity.Item2.MapID == map.MapId), Is.Zero,
                "Taking doomed output must not create a replacement or floor crate.");
            Assert.That(taken, Is.False, "Factory take must not report success for output queued by its pickup callback.");
        });
    }

    public sealed class QueueFactoryOutputOnEquipSystem : EntitySystem
    {
        public EntityUid Target;
        public bool Fired;

        public override void Initialize()
        {
            SubscribeLocalEvent<ItemComponent, GotEquippedHandEvent>(OnEquipped);
        }

        private void OnEquipped(Entity<ItemComponent> item, ref GotEquippedHandEvent args)
        {
            if (Fired || item.Owner != Target)
                return;

            Fired = true;
            QueueDel(item);
        }
    }

    public sealed class QueueSupplyCrateOnStartupSystem : EntitySystem
    {
        public bool Enabled;
        public bool Fired;
        public EntityUid ProducedCrate;

        public override void Initialize()
        {
            SubscribeLocalEvent<FrontlineSupplyCrateComponent, ComponentStartup>(OnStartup);
        }

        private void OnStartup(Entity<FrontlineSupplyCrateComponent> crate, ref ComponentStartup args)
        {
            if (!Enabled || Fired)
                return;

            Fired = true;
            ProducedCrate = crate.Owner;
            QueueDel(crate);
        }
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
        var stockpiles = server.System<FrontlineStockpileSystem>();
        var hands = server.System<SharedHandsSystem>();
        EntityUid factory = default;
        EntityUid core = default;
        EntityUid actor = default;
        EntityUid gun = default;
        EntityUid magazine = default;

        await server.WaitPost(() =>
        {
            factory = SEntMan.SpawnEntity("FrontlineFactory", map.GridCoords);
            core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var materials = stackSystem.SpawnAtPosition(25, "BasicMaterials", map.GridCoords);
            Assert.That(factorySystem.TrySubmitJob(factory, "FrontlineFactoryMk58", new[] { materials }), Is.True);
            Assert.That(factorySystem.TrySubmitJob(factory, "FrontlineFactoryMagazinePistol", new[] { materials }), Is.True);
        });
        await Pair.RunSeconds(15.2f);
        await server.WaitAssertion(() =>
        {
            Assert.That(factorySystem.GetJobs(factory), Is.Empty);
            Assert.That(SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities, Has.Count.EqualTo(2));
            Assert.That(FindPrototype("FrontlineWeaponPistolMk58", map.MapId), Is.EqualTo(EntityUid.Invalid));
            Assert.That(FindPrototype("FrontlineMagazinePistol", map.MapId), Is.EqualTo(EntityUid.Invalid));
        });
        await server.WaitPost(() =>
        {
            foreach (var product in new[] { "FrontlineWeaponPistolMk58", "MagazinePistol" })
            {
                var entity = product == "MagazinePistol" ? "FrontlineMagazinePistol" : product;
                var crate = SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities[0];
                Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Product.Id, Is.EqualTo(product));
                Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Amount, Is.EqualTo(1));
                Assert.That(factorySystem.TryTakePlayerOutput(factory, actor), Is.True);
                Assert.That(hands.GetActiveItem(actor), Is.EqualTo(crate));
                Assert.That(stockpiles.TrySubmitHeld(core, actor), Is.True);
                Assert.That(SEntMan.EntityExists(crate), Is.False);
                Assert.That(SComp<FrontlineStockpileComponent>(core).Counts[product], Is.EqualTo(1));
                Assert.That(FindPrototype(entity, map.MapId), Is.EqualTo(EntityUid.Invalid));
                Assert.That(stockpiles.TryWithdrawPlayerProduct(core, actor, product), Is.True);
                Assert.That(SComp<FrontlineStockpileComponent>(core).Counts[product], Is.Zero);
            }
            gun = FindPrototype("FrontlineWeaponPistolMk58", map.MapId);
            magazine = FindPrototype("FrontlineMagazinePistol", map.MapId);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(SComp<FrontlineFactoryComponent>(factory).OutputContainer.ContainedEntities, Is.Empty);
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
            var persistedJobs = system.GetJobs(persisted);
            Assert.That(persistedJobs, Has.Count.EqualTo(2), "Keep the invalid paid claim without assigning it a processing slot.");
            Assert.That(persistedJobs[0].Recipe.Id, Is.EqualTo("MissingFrontlineFactoryRecipe"));
            Assert.That(persistedJobs[0].Remaining, Is.EqualTo(TimeSpan.Zero));
            Assert.That(persistedJobs[1].Remaining, Is.GreaterThan(TimeSpan.Zero).And.LessThan(TimeSpan.FromSeconds(1)));
        });
        await Pair.RunSeconds(0.5f);
        await server.WaitAssertion(() =>
        {
            var jobs = system.GetJobs(persisted);
            Assert.That(jobs, Has.Count.EqualTo(1));
            Assert.That(jobs[0].Recipe.Id, Is.EqualTo("MissingFrontlineFactoryRecipe"));
            Assert.That(jobs[0].Remaining, Is.EqualTo(TimeSpan.Zero));
            var outputs = SComp<FrontlineFactoryComponent>(persisted).OutputContainer.ContainedEntities;
            Assert.That(outputs, Has.Count.EqualTo(1), "Valid paid work behind a quarantined claim must complete.");
            var crate = SComp<FrontlineSupplyCrateComponent>(outputs.Single());
            Assert.That(crate.Product.Id, Is.EqualTo("Brutepack1"));
            Assert.That(crate.Amount, Is.EqualTo(2));
        });
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
                Is.EqualTo(new Robust.Shared.Prototypes.EntProtoId("FrontlineFactoryMedicalCrate")));
        });
    }

    [Test]
    public async Task SupplyCrateIsPortableWithoutPhysicalContents()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var hands = server.System<SharedHandsSystem>();
        EntityUid crate = default;
        EntityUid user = default;

        await server.WaitPost(() =>
        {
            crate = SEntMan.SpawnEntity("FrontlineSupplyCrate", map.GridCoords);
            user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<EntityStorageComponent>(crate), Is.False);
            Assert.That(SEntMan.HasComponent<Content.Shared.Storage.StorageComponent>(crate), Is.False);
        });
        await server.WaitPost(() =>
        {
            Assert.That(hands.TryPickupAnyHand(user, crate), Is.True);
            Assert.That(hands.TryDrop(user, crate), Is.True);
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(crate), Is.True);
            Assert.That(SEntMan.System<SharedTransformSystem>().GetMapCoordinates(crate).MapId,
                Is.EqualTo(map.MapId));
        });
    }

    public sealed class OutputBuiTest : InteractionTest
    {
        [Test]
        public async Task ConnectedPlayerTakesCompletedSealedCrateThroughNativeButtonWithoutDuplication()
        {
            await SpawnTarget("FrontlineFactory");
            var factory = STarget!.Value;
            var system = Server.System<FrontlineFactorySystem>();
            var containers = Server.System<SharedContainerSystem>();
            BaseContainer output = default!;
            EntityUid crate = default;
            await Server.WaitPost(() =>
            {
                var input = Stack.SpawnAtPosition(10, "BasicMaterials", SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(system.TryInsertInput(factory, input), Is.True);
                Assert.That(system.TrySubmitContainedJob(factory, "FrontlineFactorySupplyCrate"), Is.True);
            });
            await Pair.RunSeconds(5.1f);
            await Server.WaitPost(() =>
            {
                Assert.That(system.GetJobs(factory), Is.Empty);
                Assert.That(containers.TryGetContainer(factory, "outputContainer", out output), Is.True);
                Assert.That(output.ContainedEntities, Has.Count.EqualTo(1));
                crate = output.ContainedEntities.Single();
                Assert.That(SComp<MetaDataComponent>(crate).EntityPrototype?.ID, Is.EqualTo("FrontlineSupplyCrate"));
                Assert.That(HandSys.GetActiveItem(SPlayer), Is.Null);
            });
            await Interact();
            await Pair.RunUntilSynced();
            Assert.That(TryGetBui(FrontlineFactoryUiKey.Key, out _), Is.True);
            for (var click = 0; click < 2; click++)
            {
                Button take = default!;
                await Client.WaitAssertion(() =>
                {
                    var window = GetWindow<FrontlineFactoryWindow>();
                    Assert.That(TryGetControlFromChildren<Button>(button => button.Name == "TakeOutput", window, out take),
                        Is.True, "Factory production BUI must expose a button named TakeOutput.");
                    if (click == 0)
                        Assert.That(take.Disabled, Is.False);
                });
                await ClickControl(take);
                await Pair.RunUntilSynced();
                await Server.WaitAssertion(() =>
                {
                    Assert.That(HandSys.GetActiveItem(SPlayer), Is.EqualTo(crate));
                    Assert.That(output.ContainedEntities, Is.Empty);
                    Assert.That(SEntMan.EntityExists(crate), Is.True);
                    var contents = SComp<FrontlineSupplyCrateComponent>(crate);
                    Assert.That(contents.Product.Id, Is.EqualTo("SoldierSupplies"));
                    Assert.That(contents.Amount, Is.EqualTo(5));
                    Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(crate), Is.False);
                    Assert.That(SEntMan.HasComponent<EntityStorageComponent>(crate), Is.False);
                    Assert.That(SEntMan.HasComponent<Content.Shared.Storage.StorageComponent>(crate), Is.False);
                    Assert.That(SEntMan.EntityQuery<MetaDataComponent, TransformComponent>()
                        .Count(entity => entity.Item1.EntityPrototype?.ID == "FrontlineSupplyCrate" &&
                            entity.Item2.MapID == MapId), Is.EqualTo(1));
                });
            }
            await CloseBui(FrontlineFactoryUiKey.Key);
        }
    }

    private int CountPrototype(string id) => SEntMan.EntityQuery<MetaDataComponent>()
        .Count(meta => meta.EntityPrototype?.ID == id);

    private EntityUid FindPrototype(string id, MapId mapId)
    {
        var query = SEntMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var metadata, out var transform))
        {
            if (transform.MapID == mapId && metadata.EntityPrototype?.ID == id)
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
