#nullable enable
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Destructible;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.Destructible.Thresholds.Triggers;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
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
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineLogisticsTruckTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";

    [Test]
    public async Task ProductionTruckClientLoadsAndUnloadsSealedCargo()
    {
        await SetTile(Plating, grid: MapData.Grid);
        await AddGravity();
        await AddAtmosphere();
        await SpawnTarget("FrontlineLogisticsTruck");
        var truck = STarget!.Value;
        var slots = Server.System<ItemSlotsSystem>();
        var vehicles = Server.System<VehicleSystem>();
        var cargoNet = await PlaceInHands("FrontlineFactoryWeaponCrate");
        var cargo = ToServer(cargoNet);
        string product = default!;
        int amount = default;
        string slotId = default!;

        await Server.WaitPost(() =>
        {
            var component = SEntMan.GetComponent<ItemSlotsComponent>(truck);
            Assert.That(component.Slots.Count, Is.EqualTo(10));
            Assert.That(component.Slots.Values.All(slot => !slot.HasItem && !slot.Swap), Is.True);
            slotId = component.Slots.OrderBy(pair => pair.Value.Priority).First().Key;
            var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo);
            product = crate.Product.Id;
            amount = crate.Amount;
            Assert.That(HandSys.GetActiveItem((SPlayer, Hands)), Is.EqualTo(cargo));
            AssertDriverSeatIndependent();
        });
        await Pair.RunUntilSynced();

        // Unlike the trusted API test below, both cargo actions originate on the connected client.
        // Native Use reaches ItemSlots' InteractUsing handler before AfterInteract; the
        // InteractionTest.InteractUsing helper calls server UserInteraction directly, so do not use it here.
        await PressKey(EngineKeyFunctions.Use, cursorEntity: Target);
        await RunTicks(3);
        await Server.WaitAssertion(() =>
        {
            Assert.That(slots.GetItemOrNull(truck, slotId), Is.EqualTo(cargo));
            Assert.That(SEntMan.GetComponent<ItemSlotsComponent>(truck).Slots.Values.Count(slot => slot.HasItem), Is.EqualTo(1));
            Assert.That(SEntMan.GetComponent<TransformComponent>(cargo).ParentUid, Is.EqualTo(truck));
            Assert.That(HandSys.GetActiveItem((SPlayer, Hands)), Is.Null);
            AssertCargoMetadata();
            AssertDriverSeatIndependent();
        });
        await Pair.RunUntilSynced();

        await Client.WaitPost(() =>
        {
            var slot = CEntMan.GetComponent<ItemSlotsComponent>(CTarget!.Value).Slots[slotId];
            Assert.That(CEntMan.GetNetEntity(slot.Item), Is.EqualTo(cargoNet));
            // With no slot text override, ItemSlots uses the contained entity's localized name.
            var text = slot.EjectVerbText != null ? Loc.GetString(slot.EjectVerbText)
                : slot.Name != string.Empty ? Loc.GetString(slot.Name)
                : CEntMan.GetComponent<MetaDataComponent>(ToClient(cargoNet)).EntityName;
            var verbs = Client.System<Content.Client.Verbs.VerbSystem>();
            var eject = verbs.GetLocalVerbs(CTarget.Value, CPlayer, typeof(AlternativeVerb))
                .Single(verb => verb.Text == text && verb.IconEntity == cargoNet && verb.Priority == slot.Priority
                    && verb.Category?.Text == VerbCategory.Eject.Text);
            Assert.That(eject.ClientExclusive, Is.False);
            Assert.That(eject.Disabled, Is.False);
            verbs.ExecuteVerb(CTarget.Value, eject);
        });
        await RunTicks(3);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<ItemSlotsComponent>(truck).Slots.Values.All(slot => !slot.HasItem), Is.True);
            Assert.That(slots.GetItemOrNull(truck, slotId), Is.Null);
            Assert.That(SEntMan.EntityExists(cargo), Is.True);
            // TryEjectToHands uses PickupOrDrop: this unseated player has an empty active hand.
            Assert.That(HandSys.GetActiveItem((SPlayer, Hands)), Is.EqualTo(cargo));
            Assert.That(SEntMan.GetComponent<TransformComponent>(cargo).ParentUid, Is.EqualTo(SPlayer));
            AssertCargoMetadata();
            AssertDriverSeatIndependent();
        });

        void AssertCargoMetadata()
        {
            var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo);
            Assert.That(crate.Product.Id, Is.EqualTo(product));
            Assert.That(crate.Amount, Is.EqualTo(amount));
            Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(cargo), Is.False);
        }

        void AssertDriverSeatIndependent()
        {
            Assert.That(vehicles.TryGetOperatorContainer(truck, out var seat), Is.True);
            Assert.That(seat, Is.TypeOf<ContainerSlot>());
            Assert.That(seat!.ContainedEntities, Is.Empty);
            Assert.That(SEntMan.GetComponent<VehicleComponent>(truck).Operator, Is.Null);
        }
    }

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

    [TestCase("FloorConcrete", "FloorDirt")]
    [TestCase("FrontlineFloorAsphalt", "FrontlineFloorDirt")]
    [TestCase("FrontlineFloorConcrete", "FrontlineFloorGrass")]
    [TestCase("FrontlineFloorAsphalt", "FrontlineFloorSand")]
    [TestCase("FrontlineFloorConcrete", "FrontlineFloorDirtRoad")]
    public async Task ProductionTruckClientRoadDirtRoadTravelPreservesDriverAndCargo(string road, string dirt)
    {
        await Server.WaitPost(() =>
        {
            for (var x = -2; x <= 26; x++)
            for (var y = -1; y <= 1; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y),
                    new Tile(TileMan[x >= 8 && x < 16 ? dirt : road].TileId));
        });
        await AddGravity();
        await AddAtmosphere();
        await SpawnTarget("FrontlineLogisticsTruck");
        var truck = STarget!.Value;
        var slots = Server.System<ItemSlotsSystem>();
        var vehicles = Server.System<VehicleSystem>();
        EntityUid cargo = default;
        string slotId = default!;
        string product = default!;
        int amount = default;
        await Server.WaitPost(() =>
        {
            var component = SEntMan.GetComponent<ItemSlotsComponent>(truck);
            slotId = component.Slots.Keys.OrderBy(id => id).First();
            cargo = SEntMan.SpawnEntity("FrontlineFactoryWeaponCrate", ToServer(PlayerCoords));
            var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo);
            product = crate.Product.Id;
            amount = crate.Amount;
            Assert.That(HandSys.TryPickupAnyHand(SPlayer, cargo), Is.True);
            Assert.That(slots.TryInsertFromHand(truck, component.Slots[slotId], (SPlayer, Hands)), Is.True);
        });
        await Pair.RunUntilSynced();
        await Client.WaitPost(() =>
        {
            var verbs = Client.System<Content.Client.Verbs.VerbSystem>();
            var enter = verbs.GetLocalVerbs(CTarget!.Value, CPlayer, typeof(AlternativeVerb))
                .Single(verb => verb.Text == Loc.GetString("container-vehicle-verb-enter"));
            Assert.That(enter.ClientExclusive, Is.False);
            Assert.That(enter.Disabled, Is.False);
            verbs.ExecuteVerb(CTarget.Value, enter);
        });
        await RunTicks(3);
        await Server.WaitAssertion(() => Assert.That(ActiveDoAfters.Count(), Is.EqualTo(1)));
        await Pair.RunSeconds(1.25f);
        await Server.WaitAssertion(AssertDriverAndCargo);
        await Pair.RunUntilSynced();

        // One continuous client-held input actually crosses both boundaries. Sample only
        // after warmup, with identical half-second windows; never teleport/reset velocity.
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down, cursorEntity: Target);
        try
        {
            var onRoad = await Sample(road);
            await ReachSurface(dirt);
            var offRoad = await Sample(dirt);
            await ReachSurface(road);
            var restored = await Sample(road);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(onRoad.Distance, Is.GreaterThan(0.5f));
                Assert.That(offRoad.Distance / onRoad.Distance, Is.InRange(0.48f, 0.62f),
                    "Equal steady-input travel must reflect the roadmap's 50–60% off-road speed (2% travel tolerance).");
                Assert.That(restored.Distance / onRoad.Distance, Is.InRange(0.95f, 1.05f),
                    "Returning to road must restore travel, not retain/compound the dirt modifier.");
                foreach (var sample in new[] { onRoad, restored })
                {
                    Assert.That(sample.Walk, Is.EqualTo(1f));
                    Assert.That(sample.Sprint, Is.EqualTo(1f));
                    Assert.That(sample.ClientWalk, Is.EqualTo(1f));
                    Assert.That(sample.ClientSprint, Is.EqualTo(1f));
                }
                Assert.That(offRoad.Walk, Is.InRange(0.5f, 0.6f));
                Assert.That(offRoad.Sprint, Is.InRange(0.5f, 0.6f));
                Assert.That(offRoad.ClientWalk, Is.EqualTo(offRoad.Walk).Within(0.001f));
                Assert.That(offRoad.ClientSprint, Is.EqualTo(offRoad.Sprint).Within(0.001f));
            }
        }
        finally
        {
            await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up, cursorEntity: Target);
            await RunTicks(1);
        }

        async Task ReachSurface(string surface)
        {
            for (var i = 0; i < 120; i++)
            {
                await RunTicks(5);
                var reached = false;
                await Server.WaitPost(() => reached = CurrentTile() == TileMan[surface].TileId);
                if (reached)
                    return;
            }
            Assert.Fail($"Truck did not reach {surface} within the bounded client-input budget.");
        }

        async Task<(float Distance, float Walk, float Sprint, float ClientWalk, float ClientSprint)> Sample(string surface)
        {
            await Pair.RunSeconds(0.5f);
            Vector2 start = default;
            float distance = default, walk = default, sprint = default, clientWalk = default, clientSprint = default;
            await Server.WaitPost(() =>
            {
                Assert.That(CurrentTile(), Is.EqualTo(TileMan[surface].TileId));
                start = Transform.GetWorldPosition(truck);
            });
            await Pair.RunSeconds(0.5f);
            await Server.WaitPost(() =>
            {
                Assert.That(CurrentTile(), Is.EqualTo(TileMan[surface].TileId), "Entire sample must stay on its actual surface.");
                var end = Transform.GetWorldPosition(truck);
                distance = end.X - start.X;
                Assert.That(end.Y, Is.EqualTo(start.Y).Within(0.01f));
                var movement = SEntMan.GetComponent<MovementSpeedModifierComponent>(truck);
                walk = movement.WalkSpeedModifier;
                sprint = movement.SprintSpeedModifier;
                AssertDriverAndCargo();
            });
            await Client.WaitPost(() =>
            {
                var movement = CEntMan.GetComponent<MovementSpeedModifierComponent>(CTarget!.Value);
                clientWalk = movement.WalkSpeedModifier;
                clientSprint = movement.SprintSpeedModifier;
            });
            return (distance, walk, sprint, clientWalk, clientSprint);
        }

        int CurrentTile() => MapSystem.GetTileRef(MapData.Grid,
            SEntMan.GetComponent<TransformComponent>(truck).Coordinates).Tile.TypeId;

        void AssertDriverAndCargo()
        {
            Assert.That(ServerSession!.AttachedEntity, Is.EqualTo(SPlayer));
            Assert.That(SEntMan.GetComponent<VehicleComponent>(truck).Operator, Is.EqualTo(SPlayer));
            Assert.That(SEntMan.GetComponent<VehicleOperatorComponent>(SPlayer).Vehicle, Is.EqualTo(truck));
            Assert.That(SEntMan.GetComponent<RelayInputMoverComponent>(SPlayer).RelayEntity, Is.EqualTo(truck));
            Assert.That(SEntMan.GetComponent<MovementRelayTargetComponent>(truck).Source, Is.EqualTo(SPlayer));
            Assert.That(vehicles.TryGetOperatorContainer(truck, out var seat), Is.True);
            Assert.That(seat!.ContainedEntities, Is.EqualTo(new[] { SPlayer }));
            Assert.That(SEntMan.GetComponent<MobStateComponent>(SPlayer).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(slots.GetItemOrNull(truck, slotId), Is.EqualTo(cargo));
            Assert.That(SEntMan.GetComponent<TransformComponent>(cargo).ParentUid, Is.EqualTo(truck));
            var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo);
            Assert.That(crate.Product.Id, Is.EqualTo(product));
            Assert.That(crate.Amount, Is.EqualTo(amount));
        }
    }

    [TestCase(1)]
    [TestCase(10000)]
    public async Task ProductionTruckFatalDamagePreservesDriverAndAllCargo(int excessDamage)
    {
        await SetTile(Plating, grid: MapData.Grid);
        await AddGravity();
        await AddAtmosphere();
        await SpawnTarget("FrontlineLogisticsTruck");
        var truck = STarget!.Value;
        var driver = SPlayer;
        var slots = Server.System<ItemSlotsSystem>();
        var vehicles = Server.System<VehicleSystem>();
        var containers = Server.System<SharedContainerSystem>();
        var damageable = Server.System<DamageableSystem>();
        ProtoId<DamageTypePrototype> blunt = "Blunt";
        var cargo = new EntityUid[10];
        var products = new string[9];
        var amounts = new int[9];
        var crateIds = new[]
        {
            "FrontlineSupplyCrate", "FrontlineFactoryWeaponCrate",
            "FrontlineFactoryAmmoCrate", "FrontlineFactoryMedicalCrate"
        };
        string[] slotIds = default!;

        await Server.WaitAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(SEntMan.HasComponent<DamageableComponent>(truck), Is.True,
                    "The production truck must accept native damage; do not add components in the test.");
                Assert.That(SEntMan.HasComponent<InjurableComponent>(truck), Is.True,
                    "Native DamageDealtEvent needs Injurable to apply damage and reach thresholds.");
                Assert.That(SEntMan.HasComponent<DestructibleComponent>(truck), Is.True,
                    "The production truck must configure native destruction thresholds.");
            }
        });
        // Reuse the trusted cargo setup; driver entry below still originates on the real client.
        await Server.WaitPost(() =>
        {
            var component = SEntMan.GetComponent<ItemSlotsComponent>(truck);
            slotIds = component.Slots.Keys.OrderBy(id => id).ToArray();
            Assert.That(slotIds.Length, Is.EqualTo(cargo.Length));
            Assert.That(component.Slots.Values.All(slot => !slot.HasItem && !slot.Swap && slot.EjectOnBreak), Is.True);
            for (var i = 0; i < cargo.Length; i++)
            {
                cargo[i] = SEntMan.SpawnEntity(i < products.Length ? crateIds[i % crateIds.Length] : "BasicMaterials1",
                    ToServer(PlayerCoords));
                if (i < products.Length)
                {
                    var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo[i]);
                    products[i] = crate.Product.Id;
                    amounts[i] = crate.Amount;
                }
                else
                    Stack.SetCount((cargo[i], null), 17);

                Assert.That(HandSys.TryPickupAnyHand(driver, cargo[i]), Is.True);
                Assert.That(slots.TryInsertFromHand(truck, component.Slots[slotIds[i]], (driver, Hands)), Is.True);
                Assert.That(HandSys.IsHolding(driver, cargo[i]), Is.False);
            }
        });
        await Pair.RunUntilSynced();
        await Client.WaitPost(() =>
        {
            var verbs = Client.System<Content.Client.Verbs.VerbSystem>();
            var enter = verbs.GetLocalVerbs(CTarget!.Value, CPlayer, typeof(AlternativeVerb))
                .Single(verb => verb.Text == Loc.GetString("container-vehicle-verb-enter"));
            Assert.That(enter.ClientExclusive, Is.False);
            Assert.That(enter.Disabled, Is.False);
            verbs.ExecuteVerb(CTarget.Value, enter);
        });
        await RunTicks(3);
        await Server.WaitAssertion(() => Assert.That(ActiveDoAfters.Count(), Is.EqualTo(1)));
        await Pair.RunSeconds(1.25f);
        await Server.WaitAssertion(() =>
        {
            Assert.That(ActiveDoAfters, Is.Empty);
            Assert.That(SEntMan.GetComponent<VehicleComponent>(truck).Operator, Is.EqualTo(driver));
            Assert.That(SEntMan.GetComponent<VehicleOperatorComponent>(driver).Vehicle, Is.EqualTo(truck));
            Assert.That(SEntMan.GetComponent<RelayInputMoverComponent>(driver).RelayEntity, Is.EqualTo(truck));
            Assert.That(SEntMan.GetComponent<MovementRelayTargetComponent>(truck).Source, Is.EqualTo(driver));
            Assert.That(vehicles.TryGetOperatorContainer(truck, out var seat), Is.True);
            Assert.That(seat, Is.TypeOf<ContainerSlot>());
            Assert.That(seat!.ContainedEntities, Is.EqualTo(new[] { driver }));
            Assert.That(HandSys.EnumerateHeld((driver, null)).Count(item =>
                SEntMan.TryGetComponent<VirtualItemComponent>(item, out var blocker) && blocker.BlockingEntity == truck), Is.EqualTo(1));
            Assert.That(SEntMan.GetComponent<MobStateComponent>(driver).CurrentState, Is.EqualTo(MobState.Alive));
            AssertCargo(loaded: true);
        });

        await Server.WaitPost(() =>
        {
            // Cross every configured damage threshold, including native automatic overkill.
            // Damage must perform ejection before queued deletion recursively destroys remaining children.
            var thresholds = SEntMan.GetComponent<DestructibleComponent>(truck).Thresholds
                .Select(threshold => threshold.Trigger).OfType<DamageTrigger>().ToArray();
            Assert.That(thresholds, Is.Not.Empty);
            var damage = new DamageSpecifier(ProtoMan.Index(blunt), thresholds.Max(trigger => trigger.Damage) + excessDamage);
            Assert.That(damageable.TryChangeDamage(truck, damage, ignoreResistances: true, ignoreGlobalModifiers: true), Is.True);
            Assert.That(SEntMan.IsQueuedForDeletion(truck), Is.True, "Native fatal damage must actually destroy the truck.");
        });
        await RunTicks(3);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(truck), Is.False);
            Assert.That(ServerSession!.AttachedEntity, Is.EqualTo(driver));
            Assert.That(SEntMan.EntityExists(driver), Is.True, "Destruction must eject, not delete or replace, the original body.");
            Assert.That(SEntMan.GetComponent<MobStateComponent>(driver).CurrentState, Is.EqualTo(MobState.Alive),
                "Truck destruction is separate from driver death.");
            Assert.That(SEntMan.GetComponent<TransformComponent>(driver).ParentUid, Is.EqualTo(MapData.Grid.Owner));
            Assert.That(containers.TryGetContainingContainer(driver, out _), Is.False);
            Assert.That(SEntMan.HasComponent<VehicleOperatorComponent>(driver), Is.False);
            Assert.That(SEntMan.HasComponent<RelayInputMoverComponent>(driver), Is.False);
            Assert.That(SEntMan.HasComponent<InteractionRelayComponent>(driver), Is.False);
            Assert.That(HandSys.EnumerateHeld((driver, null)).Any(item =>
                SEntMan.TryGetComponent<VirtualItemComponent>(item, out var blocker) && blocker.BlockingEntity == truck), Is.False);
            AssertCargo(loaded: false);
        });

        void AssertCargo(bool loaded)
        {
            Assert.That(cargo.Distinct().Count(), Is.EqualTo(cargo.Length));
            for (var i = 0; i < cargo.Length; i++)
            {
                Assert.That(SEntMan.EntityExists(cargo[i]), Is.True, "Every original cargo UID must survive destruction.");
                Assert.That(SEntMan.IsQueuedForDeletion(cargo[i]), Is.False);
                Assert.That(SEntMan.GetComponent<MetaDataComponent>(cargo[i]).EntityPrototype?.ID,
                    Is.EqualTo(i < products.Length ? crateIds[i % crateIds.Length] : "BasicMaterials1"));
                Assert.That(SEntMan.GetComponent<TransformComponent>(cargo[i]).ParentUid, Is.EqualTo(loaded ? truck : MapData.Grid.Owner));
                if (loaded)
                    Assert.That(slots.GetItemOrNull(truck, slotIds[i]), Is.EqualTo(cargo[i]));
                else
                    Assert.That(containers.TryGetContainingContainer(cargo[i], out _), Is.False, "Cargo must be ejected onto the floor.");
                if (i < products.Length)
                {
                    var crate = SEntMan.GetComponent<FrontlineSupplyCrateComponent>(cargo[i]);
                    Assert.That(crate.Product.Id, Is.EqualTo(products[i]));
                    Assert.That(crate.Amount, Is.EqualTo(amounts[i]));
                    Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(cargo[i]), Is.False);
                }
            }
            var material = SEntMan.GetComponent<StackComponent>(cargo[^1]);
            Assert.That(material.StackTypeId.Id, Is.EqualTo("BasicMaterials"));
            Assert.That(material.Count, Is.EqualTo(17));
            Assert.That(material.Unlimited, Is.False);
            // Exact map-scoped entity membership rejects replacement copies and extra cargo spawns.
            var count = 0;
            var query = SEntMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var metadata, out var transform))
            {
                if (transform.MapID != MapId || metadata.EntityPrototype is not { } prototype ||
                    !(prototype.ID == "BasicMaterials1" || crateIds.Contains(prototype.ID)))
                    continue;
                Assert.That(cargo, Does.Contain(uid));
                count++;
            }
            Assert.That(count, Is.EqualTo(cargo.Length));
        }
    }
}
