#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Content.IntegrationTests.Tests.Destructible;
using Content.IntegrationTests.Tests.DeviceNetwork;
using Content.Server.GameTicking;
using Content.Server.Stack;
using Content.Server.War;
using Content.Shared.CCVar;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class PersistentWarOrderlyShutdownTest : RobustIntegrationTest
{
    [Test]
    public async Task NativeOrderlyShutdownSavesPaidFactoryClaimsBeforeEntityFlush()
    {
        // Reuse the content pool's boot inputs, not a borrowed pair or GameTest's live-server teardown.
        var options = new ServerIntegrationOptions
        {
            Pool = false,
            Asynchronous = true,
            ContentStart = true,
            LoadTestAssembly = false,
            ContentAssemblies = PoolManager.Instance.ServerAssemblies,
            ExtraPrototypeList = PoolManager.Instance.TestPrototypes.ToList(),
            Options = new() { LoadConfigAndUserData = false },
            BeforeStart = () =>
            {
                var systems = IoCManager.Resolve<IEntitySystemManager>();
                systems.LoadExtraSystemType<DeviceNetworkTestSystem>();
                systems.LoadExtraSystemType<TestDestructibleListenerSystem>();
            },
        };
        foreach (var (name, value) in PoolManager.Instance.DefaultCvars)
            options.CVarOverrides[name] = value;
        options.CVarOverrides[CCVars.GameDummyTicker.Name] = "false";
        options.CVarOverrides[CCVars.GameLobbyEnabled.Name] = "true";

        var server = StartServer(options);
        EntityManager? entities = null;
        Action? beforeFlush = null;
        Action? afterFlush = null;
        try
        {
            await server.WaitIdleAsync();
            entities = server.EntMan;
            // This native virtual provider remains readable after FinishMainLoop and IoCManager.Clear.
            var data = server.Resolve<IResourceManager>().UserData;
            Assert.That(data, Is.TypeOf<VirtualWritableDirProvider>());
            IConsoleHost console = server.ConsoleHost;
            Assert.That(console.AvailableCommands.ContainsKey("shutdown"), Is.True);
            var ticker = server.System<GameTicker>();
            var war = server.System<WarStateSystem>();
            var factories = server.System<FrontlineFactorySystem>();
            var containers = server.System<SharedContainerSystem>();
            var stacks = server.System<StackSystem>();
            ProtoId<FrontlineFactoryRecipePrototype> recipe = "FrontlineFactoryBrutepack";
            ProtoId<FrontlineSupplyProductPrototype> product = "SoldierSupplies";
            EntityUid map = EntityUid.Invalid;
            EntityUid factory = EntityUid.Invalid;
            EntityUid input = EntityUid.Invalid;
            EntityUid output = EntityUid.Invalid;
            WarState? campaign = null;
            List<WarFactorySnapshot>? paidClaims = null;
            var insertedInput = false;
            var submitted = false;
            var insertedOutput = false;
            var defaultProduct = "";
            var defaultAmount = 0;

            await server.WaitPost(() =>
            {
                ticker.RestartRound(); // Match the content TestPair's native initialization.
                war.StartNewWar();
                ticker.SetGamePreset("PersistentWar");
                ticker.StartRound(true);
                campaign = war.State;
                map = server.System<SharedMapSystem>().GetMap(ticker.DefaultMap);
                factory = entities.EntityQuery<FrontlineFactoryComponent>().Single(component =>
                    component.FactoryId == "frontline-test-factory-west" &&
                    server.Transform(component.Owner).MapID == ticker.DefaultMap).Owner;
                var coordinates = server.Transform(factory).Coordinates;
                input = stacks.SpawnAtPosition(8, "BasicMaterials", coordinates);
                insertedInput = factories.TryInsertInput(factory, input);
                submitted = factories.TrySubmitContainedJob(factory, recipe);
                output = entities.SpawnEntity("FrontlineFactoryMedicalCrate", coordinates);
                var crate = entities.GetComponent<FrontlineSupplyCrateComponent>(output);
                defaultProduct = crate.Product.Id;
                defaultAmount = crate.Amount;
                // Permitted per-entity entitlement; do not alter the recipe or shared prototype.
                crate.Product = product;
                crate.Amount = 7;
                insertedOutput = containers.Insert(output,
                    entities.GetComponent<FrontlineFactoryComponent>(factory).OutputContainer);
                paidClaims = factories.CaptureSnapshot(ticker.DefaultMap);
            });

            Assert.That(ticker.CurrentPreset?.ID, Is.EqualTo("PersistentWar"));
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(campaign, Is.Not.Null);
            Assert.That(campaign!.Status, Is.EqualTo(WarStatus.Active));
            Assert.That(insertedInput && submitted && insertedOutput, Is.True);
            Assert.That(defaultProduct, Is.EqualTo("Brutepack1"));
            Assert.That(defaultAmount, Is.EqualTo(2));
            Assert.That(entities.GetComponent<StackComponent>(input).Count, Is.EqualTo(3),
                "The native queue submission must really spend five of eight materials.");
            Assert.That(paidClaims, Is.Not.Null);
            Assert.That(paidClaims!.Select(claim => claim.FactoryId), Is.EquivalentTo(new[]
                { "frontline-test-factory-west", "frontline-test-factory-east" }));
            var paid = paidClaims.Single(claim => claim.FactoryId == "frontline-test-factory-west");
            var remaining = factories.GetJobs(factory).Single().Remaining.Ticks;
            Assert.That(remaining, Is.GreaterThan(0));
            Assert.That(paid.Prototype, Is.EqualTo("FrontlineFactory"));
            Assert.That(paid.Jobs.Select(job => (job.Recipe, job.RemainingTicks)),
                Is.EqualTo(new[] { (recipe.Id, remaining) }));
            Assert.That(paid.Inputs.Select(stack => (stack.StackId, stack.Prototype, stack.Count)),
                Is.EqualTo(new[] { ("BasicMaterials", "BasicMaterials1", 3) }));
            Assert.That(paid.Outputs.Select(crate => (crate.Prototype, crate.Product, crate.Amount)),
                Is.EqualTo(new[] { ("FrontlineFactoryMedicalCrate", product.Id, 7) }));
            var empty = paidClaims.Single(claim => claim.FactoryId == "frontline-test-factory-east");
            Assert.That(empty.Jobs, Is.Empty);
            Assert.That(empty.Inputs, Is.Empty);
            Assert.That(empty.Outputs, Is.Empty);
            Assert.That(data.Exists(WarStrategicSnapshotSystem.SavePath), Is.False,
                "The shutdown assertion must not pass by reading a prior restart's save.");

            var flushCount = 0;
            var shuttingDown = false;
            var claimsLive = false;
            var deletedAfterFlush = false;
            string? presetAtFlush = null;
            WarState? campaignAtFlush = null;
            List<WarFactorySnapshot>? claimsAtFlush = null;
            Exception? observationFailure = null;
            beforeFlush = () =>
            {
                // Observe the real engine boundary; never throw assertions into destructive cleanup.
                try
                {
                    flushCount++;
                    shuttingDown = entities.ShuttingDown;
                    claimsLive = new[] { map, factory, input, output }.All(entities.EntityExists);
                    presetAtFlush = ticker.CurrentPreset?.ID;
                    campaignAtFlush = war.State;
                    claimsAtFlush = factories.CaptureSnapshot(ticker.DefaultMap);
                }
                catch (Exception e)
                {
                    observationFailure = e;
                }
            };
            afterFlush = () =>
            {
                try
                {
                    deletedAfterFlush = new[] { map, factory, input, output }.All(uid => !entities.EntityExists(uid));
                }
                catch (Exception e)
                {
                    observationFailure ??= e;
                }
            };
            await server.WaitPost(() =>
            {
                entities.BeforeEntityFlush += beforeFlush;
                entities.AfterEntityFlush += afterFlush;
                console.ExecuteCommand("shutdown strategic-persistence-regression");
            });
            // WaitPost can acknowledge before cleanup. Stop forces WaitIdle to consume ShutDownMessage,
            // emitted only AFTER the asynchronous server's FinishMainLoop and IoCManager.Clear.
            server.Stop();
            await server.WaitIdleAsync(throwOnUnhandled: false);

            Assert.That(server.IsAlive, Is.False);
            Assert.That(server.UnhandledException, Is.Null);
            Assert.That(observationFailure, Is.Null);
            Assert.That(flushCount, Is.EqualTo(1));
            Assert.That(shuttingDown && claimsLive, Is.True);
            Assert.That(presetAtFlush, Is.EqualTo("PersistentWar"));
            Assert.That(campaignAtFlush, Is.EqualTo(campaign));
            Assert.That(JsonSerializer.Serialize(claimsAtFlush), Is.EqualTo(JsonSerializer.Serialize(paidClaims)),
                "The campaign map and exact paid claims must still exist at native pre-flush capture.");
            Assert.That(deletedAfterFlush, Is.True, "Native shutdown must actually delete the campaign and paid entities.");

            Assert.That(data.Exists(WarStrategicSnapshotSystem.SavePath), Is.True,
                "Orderly native shutdown must save strategic claims before flushing the live campaign.");
            using var stream = data.OpenRead(WarStrategicSnapshotSystem.SavePath);
            var saved = JsonSerializer.Deserialize<WarStrategicSnapshot>(stream);
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.SnapshotVersion, Is.EqualTo(WarStrategicSnapshotSystem.SnapshotVersion));
            Assert.That(saved.WarId, Is.EqualTo(campaign.WarId));
            Assert.That(JsonSerializer.Serialize(saved.Factories.OrderBy(claim => claim.FactoryId)),
                Is.EqualTo(JsonSerializer.Serialize(claimsAtFlush!.OrderBy(claim => claim.FactoryId))),
                "Persist stable factory IDs, exact paid recipe ticks, unused input and actual non-default output.");
        }
        finally
        {
            // Own cleanup on setup/assertion failures too; never return this stopped host to a pool.
            server.Stop();
            try
            {
                await server.WaitIdleAsync(throwOnUnhandled: false);
            }
            finally
            {
                if (entities != null)
                {
                    entities.BeforeEntityFlush -= beforeFlush;
                    entities.AfterEntityFlush -= afterFlush;
                }
                server.Dispose(); // IntegrationInstance.Dispose only calls Stop; safe after the barrier.
            }
        }
    }
}
