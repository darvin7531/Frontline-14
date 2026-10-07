#nullable enable
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Item;
using Content.Shared.Movement.Components;
using Content.Shared.Stacks;
using Content.Shared.Vehicle.Components;
using Content.Shared.Vehicle.Systems;
using Content.Shared.Verbs;
using Content.Shared.War;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineLogisticsTruckTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";

    [Test]
    public async Task ProductionTruckClientDrivingPreservesTrustedApiCargo()
    {
        for (var x = -2; x <= 8; x++)
        for (var y = -2; y <= 5; y++)
            await SetTile(Plating, FromServer(ToServer(PlayerCoords).Offset(new Vector2(x, y))), MapData.Grid);
        await AddGravity();
        await AddAtmosphere();
        await SpawnTarget("FrontlineLogisticsTruck");
        var truck = STarget!.Value;
        var slots = Server.System<ItemSlotsSystem>();
        var vehicles = Server.System<VehicleSystem>();
        var cargo = new EntityUid[10];
        var products = new string[9];
        var amounts = new int[9];
        var crateIds = new[]
        {
            "FrontlineSupplyCrate", "FrontlineFactoryWeaponCrate",
            "FrontlineFactoryAmmoCrate", "FrontlineFactoryMedicalCrate"
        };
        string[] slotIds = default!;
        EntityUid overflow = default;
        Vector2 start = default;
        Vector2 turnStart = default;
        double heading = default;

        // Trusted server ItemSlots APIs characterize physical cargo, not client load/unload requests.
        await Server.WaitPost(() =>
        {
            var component = SEntMan.GetComponent<ItemSlotsComponent>(truck);
            slotIds = component.Slots.Keys.OrderBy(id => id).ToArray();
            Assert.That(slotIds.Length, Is.EqualTo(10));
            Assert.That(component.Slots.Values.All(slot => !slot.HasItem && !slot.Swap), Is.True);
            var movement = SEntMan.GetComponent<MovementSpeedModifierComponent>(truck);
            Assert.That(movement.BaseWalkSpeed, Is.EqualTo(3));
            Assert.That(movement.BaseSprintSpeed, Is.EqualTo(4.5f));
            Assert.That(movement.Acceleration, Is.EqualTo(movement.BaseAcceleration));
            Assert.That(movement.Friction, Is.EqualTo(movement.BaseFriction * Server.CfgMan.GetCVar(CCVars.TileFrictionModifier)));

            for (var i = 0; i < cargo.Length; i++)
            {
                cargo[i] = SEntMan.SpawnEntity(i < products.Length ? crateIds[i % crateIds.Length] : "BasicMaterials1",
                    ToServer(PlayerCoords));
                if (i < products.Length)
                {
                    var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo[i]);
                    products[i] = crate.Product.Id;
                    amounts[i] = crate.Amount;
                    Assert.That(SEntMan.GetComponent<ItemComponent>(cargo[i]).Size.Id, Is.EqualTo("Huge"));
                }
                else
                    Stack.SetCount((cargo[i], null), 17);

                Assert.That(HandSys.TryPickupAnyHand(SPlayer, cargo[i]), Is.True);
                Assert.That(slots.TryInsertFromHand(truck, component.Slots[slotIds[i]], (SPlayer, Hands)), Is.True);
                Assert.That(HandSys.IsHolding(SPlayer, cargo[i]), Is.False);
                Assert.That(slots.GetItemOrNull(truck, slotIds[i]), Is.EqualTo(cargo[i]));
            }

            overflow = SEntMan.SpawnEntity("FrontlineSupplyCrate", ToServer(PlayerCoords));
            Assert.That(HandSys.TryPickupAnyHand(SPlayer, overflow), Is.True);
            Assert.That(slots.TryInsertEmpty(truck, overflow, SPlayer), Is.False);
            Assert.That(HandSys.IsHolding(SPlayer, overflow), Is.True, "Full cargo must not consume the held crate.");
            Assert.That(HandSys.TryDrop(SPlayer, overflow), Is.True);
            Assert.That(vehicles.TryGetOperatorContainer(truck, out var seat), Is.True);
            Assert.That(seat, Is.TypeOf<ContainerSlot>());
            Assert.That(seat!.ContainedEntities, Is.Empty, "Cargo slots must not become driver seats.");
            Assert.That(SEntMan.GetComponent<VehicleComponent>(truck).Operator, Is.Null);
            start = Transform.GetWorldPosition(truck);
        });

        // Cargo adds eject verbs; select the native entry verb explicitly rather than assuming a single verb.
        await Pair.RunUntilSynced();
        await Client.WaitPost(() =>
        {
            var verbs = Client.System<Content.Client.Verbs.VerbSystem>();
            var enter = verbs.GetLocalVerbs(CTarget!.Value, CPlayer, typeof(AlternativeVerb))
                .Single(verb => verb.Text == Loc.GetString("container-vehicle-verb-enter"));
            Assert.That(enter.ClientExclusive, Is.False);
            verbs.ExecuteVerb(CTarget.Value, enter);
        });
        await RunTicks(3);
        await Server.WaitAssertion(() => Assert.That(ActiveDoAfters.Count(), Is.EqualTo(1)));
        await Pair.RunSeconds(1.25f);
        await Server.WaitAssertion(() =>
        {
            Assert.That(ActiveDoAfters, Is.Empty);
            Assert.That(SEntMan.GetComponent<VehicleComponent>(truck).Operator, Is.EqualTo(SPlayer));
            Assert.That(SEntMan.GetComponent<RelayInputMoverComponent>(SPlayer).RelayEntity, Is.EqualTo(truck));
            Assert.That(vehicles.TryGetOperatorContainer(truck, out var seat), Is.True);
            Assert.That(seat!.ContainedEntities, Is.EqualTo(new[] { SPlayer }));
            AssertCargo();
        });
        await Pair.RunUntilSynced();
        await PressKey(EngineKeyFunctions.MoveRight, 15, cursorEntity: Target);
        await Server.WaitAssertion(() => Assert.That(Transform.GetWorldPosition(truck).X, Is.GreaterThan(start.X + 0.05f)));
        await Server.WaitPost(() =>
        {
            turnStart = Transform.GetWorldPosition(truck);
            heading = Transform.GetWorldRotation(truck).Theta;
        });
        await PressKey(EngineKeyFunctions.MoveUp, 15, cursorEntity: Target);
        await Server.WaitAssertion(() =>
        {
            Assert.That(Transform.GetWorldPosition(truck).Y, Is.GreaterThan(turnStart.Y + 0.05f));
            Assert.That(Transform.GetWorldRotation(truck).Theta, Is.Not.EqualTo(heading));
            AssertCargo();
        });
        await Pair.RunSeconds(0.5f);
        await Server.WaitPost(() => start = Transform.GetWorldPosition(truck));
        await Pair.RunSeconds(0.5f);
        await Server.WaitAssertion(() => Assert.That(Vector2.Distance(Transform.GetWorldPosition(truck), start), Is.LessThan(0.01f)));

        await Server.WaitPost(() =>
        {
            Assert.That(vehicles.TryExit(truck), Is.True);
            for (var i = 0; i < cargo.Length; i++)
            {
                Assert.That(slots.TryEject(truck, slotIds[i], null, out var unloaded), Is.True);
                Assert.That(unloaded, Is.EqualTo(cargo[i]));
                Assert.That(slots.TryEject(truck, slotIds[i], null, out _), Is.False);
            }
        });
        await RunTicks(3);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<ItemSlotsComponent>(truck).Slots.Values.All(slot => !slot.HasItem), Is.True);
            Assert.That(SEntMan.GetComponent<VehicleComponent>(truck).Operator, Is.Null);
            Assert.That(SEntMan.EntityExists(SPlayer), Is.True);
            Assert.That(SEntMan.EntityExists(overflow), Is.True);
            for (var i = 0; i < cargo.Length; i++)
            {
                Assert.That(SEntMan.EntityExists(cargo[i]), Is.True);
                Assert.That(SEntMan.GetComponent<TransformComponent>(cargo[i]).ParentUid, Is.Not.EqualTo(truck));
            }
            AssertMetadata();
        });

        void AssertCargo()
        {
            for (var i = 0; i < cargo.Length; i++)
            {
                Assert.That(slots.GetItemOrNull(truck, slotIds[i]), Is.EqualTo(cargo[i]));
                Assert.That(SEntMan.GetComponent<TransformComponent>(cargo[i]).ParentUid, Is.EqualTo(truck));
            }
            AssertMetadata();
        }

        void AssertMetadata()
        {
            for (var i = 0; i < products.Length; i++)
            {
                var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo[i]);
                Assert.That(crate.Product.Id, Is.EqualTo(products[i]));
                Assert.That(crate.Amount, Is.EqualTo(amounts[i]));
                Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(cargo[i]), Is.False,
                    "Sealed crates carry metadata, not spawned product contents.");
            }
            var material = SEntMan.GetComponent<StackComponent>(cargo[^1]);
            Assert.That(material.StackTypeId.Id, Is.EqualTo("BasicMaterials"));
            Assert.That(material.Count, Is.EqualTo(17));
            Assert.That(material.Unlimited, Is.False);
        }
    }
}
