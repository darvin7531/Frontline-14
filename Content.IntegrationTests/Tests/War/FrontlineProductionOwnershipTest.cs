#nullable enable
using System.Linq;
using System.Text.Json;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.War;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineProductionOwnershipTest : InteractionTest
{
    [Test]
    public async Task NativeProductionSerializationPreservesOwnershipReceiptsAndClaims()
    {
        await Server.WaitAssertion(() =>
        {
            var serialization = Server.ResolveDependency<ISerializationManager>();
            var id = Guid.Parse("c2a3ca8a-7c94-4454-a634-470cc98beeb1");
            var owner = ServerSession!.UserId.ToString();
            var factory = new FrontlineFactoryComponent
            {
                FactoryId = "roundtrip-factory",
                Jobs = [new FrontlineFactoryJob
                {
                    Id = id, Recipe = "FrontlineFactoryBrutepack", Remaining = TimeSpan.FromSeconds(3),
                    Access = FrontlineProductionAccess.Personal, Owner = owner,
                    PaidInputs = new() { ["BasicMaterials"] = 5 },
                }],
            };
            var restoredFactory = serialization.Read<FrontlineFactoryComponent>(serialization.WriteValue(factory, alwaysWrite: true, notNullableOverride: true), notNullableOverride: true);
            var factoryJob = restoredFactory.Jobs.Single();
            Assert.That(restoredFactory.FactoryId, Is.EqualTo(factory.FactoryId));
            Assert.That(factoryJob.Id, Is.EqualTo(id));
            Assert.That(factoryJob.Recipe, Is.EqualTo(factory.Jobs[0].Recipe));
            Assert.That(factoryJob.Remaining, Is.EqualTo(factory.Jobs[0].Remaining));
            Assert.That(factoryJob.Access, Is.EqualTo(FrontlineProductionAccess.Personal));
            Assert.That(factoryJob.Owner, Is.EqualTo(owner));
            Assert.That(factoryJob.Legacy, Is.False);
            Assert.That(factoryJob.PaidInputs, Is.EqualTo(factory.Jobs[0].PaidInputs));

            var refinery = new FrontlineRefineryComponent
            {
                RefineryId = "roundtrip-refinery",
                Jobs = [new FrontlineRefineryJob
                {
                    Id = id, Recipe = "FrontlineSteel", Remaining = TimeSpan.FromSeconds(7), Batches = 68,
                    Access = FrontlineProductionAccess.Personal, Owner = owner,
                    PaidInputs = new() { ["FrontlineRawIron"] = 5 },
                }],
            };
            var restoredRefinery = serialization.Read<FrontlineRefineryComponent>(serialization.WriteValue(refinery, alwaysWrite: true, notNullableOverride: true), notNullableOverride: true);
            var refineryJob = restoredRefinery.Jobs.Single();
            Assert.That(restoredRefinery.RefineryId, Is.EqualTo(refinery.RefineryId));
            Assert.That(refineryJob.Id, Is.EqualTo(id));
            Assert.That(refineryJob.Recipe, Is.EqualTo(refinery.Jobs[0].Recipe));
            Assert.That(refineryJob.Remaining, Is.EqualTo(refinery.Jobs[0].Remaining));
            Assert.That(refineryJob.Batches, Is.EqualTo(68));
            Assert.That(refineryJob.Access, Is.EqualTo(FrontlineProductionAccess.Personal));
            Assert.That(refineryJob.Owner, Is.EqualTo(owner));
            Assert.That(refineryJob.Legacy, Is.False);
            Assert.That(refineryJob.PaidInputs, Is.EqualTo(refinery.Jobs[0].PaidInputs));

            foreach (var invalidId in new[] { "not-a-guid", id.ToString("N") })
            {
                Assert.Throws<FormatException>(() => serialization.Read<FrontlineFactoryJob>(
                    new MappingDataNode().Add("id", invalidId).Add("recipe", "FrontlineFactoryBrutepack"), notNullableOverride: true));
                Assert.Throws<FormatException>(() => serialization.Read<FrontlineRefineryJob>(
                    new MappingDataNode().Add("id", invalidId).Add("recipe", "FrontlineSteel"), notNullableOverride: true));
            }

            var completed = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567);
            var claim = new FrontlineProductionClaimComponent { OwnerUserId = owner, CompletedAtUtc = completed };
            var restoredClaim = serialization.Read<FrontlineProductionClaimComponent>(serialization.WriteValue(claim, alwaysWrite: true, notNullableOverride: true), notNullableOverride: true);
            Assert.That(restoredClaim.OwnerUserId, Is.EqualTo(owner));
            Assert.That(restoredClaim.CompletedAtUtc, Is.EqualTo(completed));
            Assert.That(restoredClaim.CompletedAtUtcTicks, Is.EqualTo(completed.UtcTicks));
            Assert.That(restoredClaim.IsPersonal(completed.AddHours(2).AddTicks(-1)), Is.True);
            Assert.That(restoredClaim.IsPersonal(completed.AddHours(2)), Is.False);
        });
    }

    [Test]
    [NonParallelizable]
    public async Task PartialRefineryCancellationRefundsOriginalReceiptWithoutCompletedGoods()
    {
        await SpawnTarget("FrontlineRefinery");
        var uid = STarget ?? throw new AssertionException("Refinery missing.");
        var system = Server.System<FrontlineRefinerySystem>();
        var recipe = SProtoMan.Index(new ProtoId<FrontlineRefineryRecipePrototype>("FrontlineSteel"));
        var original = recipe.Input.ToDictionary(entry => entry.Key, entry => entry.Value);
        try
        {
            await Server.WaitAssertion(() =>
            {
                for (var i = 0; i < 20; i++)
                    Assert.That(system.TryInsertInput(uid, Stack.SpawnAtPosition(25, "FrontlineRawIron", SEntMan.GetCoordinates(PlayerCoords))), Is.True);
                Assert.That(system.TrySubmitPlayerJob(uid, SPlayer, recipe.ID, 100, personal: true), Is.True);
                var component = SComp<FrontlineRefineryComponent>(uid);
                var job = component.Jobs.Single();
                job.Remaining = TimeSpan.Zero;
                system.Update(0);
                Assert.That(job.Batches, Is.EqualTo(68));
                Assert.That(component.OutputContainer.ContainedEntities.Sum(output => SComp<StackComponent>(output).Count), Is.EqualTo(160));
                recipe.Input["FrontlineRawIron"] = 7;
                Assert.That(system.TryCancelPlayerJob(uid, SPlayer, job.Id, personal: false), Is.False);
                Assert.That(system.TryCancelPlayerJob(uid, SPlayer, job.Id, personal: true), Is.True);
                Assert.That(component.Jobs, Is.Empty);
                Assert.That(component.InputContainer.ContainedEntities.Sum(input => SComp<StackComponent>(input).Count), Is.EqualTo(340));
                Assert.That(component.OutputContainer.ContainedEntities.Sum(output => SComp<StackComponent>(output).Count), Is.EqualTo(160));
                Assert.That(system.TryCancelPlayerJob(uid, SPlayer, job.Id, personal: true), Is.False, "Receipts refund only once.");
            });
        }
        finally { await Server.WaitPost(() => recipe.Input = original); }
    }

    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    public async Task PublicBusySubmissionAndCancellationUseCommonPhysicalInput(string prototype)
    {
        await SpawnTarget(prototype);
        var uid = STarget ?? throw new AssertionException("Machine missing.");
        await Server.WaitAssertion(() =>
        {
            var factories = Server.System<FrontlineFactorySystem>();
            var refineries = Server.System<FrontlineRefinerySystem>();
            for (var i = 0; i < 2; i++)
            {
                var input = Stack.SpawnAtPosition(10, prototype == "FrontlineFactory" ? "BasicMaterials" : "FrontlineRawIron", SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(prototype == "FrontlineFactory" ? factories.TryInsertInput(uid, input) : refineries.TryInsertInput(uid, input), Is.True);
                Assert.That(prototype == "FrontlineFactory" ? factories.TrySubmitPlayerJob(uid, SPlayer, "FrontlineFactoryBrutepack") :
                    refineries.TrySubmitPlayerJob(uid, SPlayer, "FrontlineSteel"), Is.True);
            }
            var id = prototype == "FrontlineFactory" ? factories.GetJobs(uid).First().Id : refineries.GetJobs(uid).First().Id;
            Assert.That(prototype == "FrontlineFactory" ? factories.TryCancelPlayerJob(uid, SPlayer, id) : refineries.TryCancelPlayerJob(uid, SPlayer, id), Is.True);
            Assert.That(prototype == "FrontlineFactory" ? factories.GetJobs(uid).Count : refineries.GetJobs(uid).Count, Is.EqualTo(1));
            var inputContainer = prototype == "FrontlineFactory" ? SComp<FrontlineFactoryComponent>(uid).InputContainer : SComp<FrontlineRefineryComponent>(uid).InputContainer;
            Assert.That(inputContainer.ContainedEntities.Sum(input => SComp<StackComponent>(input).Count), Is.EqualTo(15));
        });
    }

    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    public async Task LegacyPublicWorkCompletesButCannotRefundInventedPrices(string prototype)
    {
        await SpawnTarget(prototype);
        var uid = STarget ?? throw new AssertionException("Machine missing.");
        await Server.WaitAssertion(() =>
        {
            if (prototype == "FrontlineFactory")
            {
                var system = Server.System<FrontlineFactorySystem>();
                var input = Stack.SpawnAtPosition(5, "BasicMaterials", SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(system.TryInsertInput(uid, input) && system.TrySubmitPlayerJob(uid, SPlayer, "FrontlineFactoryBrutepack"), Is.True);
                var job = SComp<FrontlineFactoryComponent>(uid).Jobs.Single();
                job.Legacy = true;
                job.PaidInputs.Clear();
                Assert.That(system.BuildUiState(uid).Jobs.Single().CanCancel, Is.False);
                Assert.That(system.TryCancelPlayerJob(uid, SPlayer, job.Id), Is.False);
                job.Remaining = TimeSpan.Zero;
                system.Update(0);
                Assert.That(system.GetJobs(uid), Is.Empty);
                Assert.That(system.BuildUiState(uid).Outputs.Single().Count, Is.EqualTo(1));
            }
            else
            {
                var system = Server.System<FrontlineRefinerySystem>();
                var input = Stack.SpawnAtPosition(5, "FrontlineRawIron", SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(system.TryInsertInput(uid, input) && system.TrySubmitPlayerJob(uid, SPlayer, "FrontlineSteel"), Is.True);
                var job = SComp<FrontlineRefineryComponent>(uid).Jobs.Single();
                job.Legacy = true;
                job.PaidInputs.Clear();
                Assert.That(system.BuildUiState(uid).Jobs.Single().CanCancel, Is.False);
                Assert.That(system.TryCancelPlayerJob(uid, SPlayer, job.Id), Is.False);
                job.Remaining = TimeSpan.Zero;
                system.Update(0);
                Assert.That(system.GetJobs(uid), Is.Empty);
                Assert.That(system.BuildUiState(uid).Outputs.Single().Amount, Is.EqualTo(5));
            }
        });
    }

    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    public async Task CompletionClaimKeepsExactUtcBoundaryAndDestructionConsumesPrivateGoods(string prototype)
    {
        await SpawnTarget(prototype);
        var uid = STarget ?? throw new AssertionException("Machine missing.");
        var production = Server.System<FrontlineProductionSystem>();
        EntityUid output = default;
        await Server.WaitAssertion(() =>
        {
            if (prototype == "FrontlineFactory")
            {
                var system = Server.System<FrontlineFactorySystem>();
                var input = Stack.SpawnAtPosition(10, "BasicMaterials", SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(system.TryInsertInput(uid, input), Is.True);
                Assert.That(system.TrySubmitPlayerJob(uid, SPlayer, "FrontlineFactoryBrutepack", personal: true), Is.True);
                SComp<FrontlineFactoryComponent>(uid).Jobs.Single().Remaining = TimeSpan.Zero;
                system.Update(0);
                output = SComp<FrontlineFactoryComponent>(uid).OutputContainer.ContainedEntities.Single();
                Assert.That(system.TrySubmitPlayerJob(uid, SPlayer, "FrontlineFactoryBrutepack", personal: true), Is.True);
            }
            else
            {
                var system = Server.System<FrontlineRefinerySystem>();
                var input = Stack.SpawnAtPosition(10, "FrontlineRawIron", SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(system.TryInsertInput(uid, input), Is.True);
                Assert.That(system.TrySubmitPlayerJob(uid, SPlayer, "FrontlineSteel", personal: true), Is.True);
                SComp<FrontlineRefineryComponent>(uid).Jobs.Single().Remaining = TimeSpan.Zero;
                system.Update(0);
                output = SComp<FrontlineRefineryComponent>(uid).OutputContainer.ContainedEntities.Single();
                Assert.That(system.TrySubmitPlayerJob(uid, SPlayer, "FrontlineSteel", personal: true), Is.True);
            }
            var claim = production.CaptureClaim(output) ?? throw new AssertionException("Personal completion did not record its timestamp.");
            var roundTrip = JsonSerializer.Deserialize<WarProductionOutputClaim>(JsonSerializer.Serialize(claim));
            Assert.That(roundTrip, Is.EqualTo(claim), "Offline restart must not reset the timestamp.");
            var owner = ServerSession!.UserId.ToString();
            Assert.That(production.CanAccess(output, owner, claim.CompletedAtUtc.AddHours(2).AddTicks(-1)), Is.True);
            Assert.That(production.CanAccess(output, null, claim.CompletedAtUtc.AddHours(2).AddTicks(-1)), Is.False);
            Assert.That(production.CanAccess(output, null, claim.CompletedAtUtc.AddHours(2)), Is.True);
            Assert.That(production.CanAccess(output, owner, claim.CompletedAtUtc.AddHours(2)), Is.False);
            SEntMan.DeleteEntity(uid);
            Assert.That(SEntMan.Deleted(output) || SEntMan.IsQueuedForDeletion(output), Is.True, "Private goods die with their machine, not on the floor.");
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() => Assert.That(SEntMan.EntityExists(output), Is.False));
    }
}
