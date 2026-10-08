#nullable enable
using System.Linq;
using System.Numerics;
using Content.Client.Gameplay;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.GameTicking;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Vehicle.Components;
using Content.Shared.Vehicle.Systems;
using Content.Shared.Verbs;
using Robust.Client.Console;
using Robust.Client.State;
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineVehicleTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";
    public override PoolSettings PoolSettings => new() { Connected = true, DummyTicker = false, Dirty = true };

    // Native composition only: one physical driver slot, no mech/key/cargo systems.
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: FrontlineVehicleTestVehicle
  name: test vehicle
  components:
  - type: Sprite
    sprite: Objects/Vehicles/janicart.rsi
    state: vehicle
  - type: Clickable
  - type: InputMover
  - type: MobMover
  - type: MovementSpeedModifier
    baseWalkSpeed: 2
    baseSprintSpeed: 3
    baseFriction: 20
  - type: GravityAffected
  - type: Physics
    bodyType: KinematicController
  - type: Fixtures
    fixtures:
      body:
        shape: !type:PhysShapeCircle
          radius: 0.35
        density: 100
        mask:
        - MobMask
        layer:
        - MobLayer
  - type: Vehicle
  - type: VehicleHandBlocker
    blockedHands: 1
  - type: ContainerVehicle
    containerId: driver
    ejectOperatorOnDisconnect: true
    ejectOperatorOnDeath: true
  - type: ContainerVehicleEntry
    entryDelay: 1
  - type: ContainerContainer
    containers:
      driver: !type:ContainerSlot
";

    public override async Task DoSetup()
    {
        await base.DoSetup();
        // InteractionTest attaches the human directly; register its native ticker lifecycle too.
        await Server.WaitPost(() => Server.System<GameTicker>().PlayerJoinGame(ServerSession!));
        await RunTicks(5);
        for (var x = -8; x <= 8; x++)
        for (var y = -2; y <= 2; y++)
            await SetTile(Plating, FromServer(ToServer(PlayerCoords).Offset(new Vector2(x, y))), MapData.Grid);
        await AddGravity();
        await AddAtmosphere();
        await SpawnTarget("FrontlineVehicleTestVehicle");
    }

    [Test]
    public async Task NativeSingleSeatRelaysOnlyItsDriverAndClientCanExit()
    {
        var vehicle = STarget!.Value;
        var vehicles = Server.System<VehicleSystem>();
        Vector2 vehicleStart = default;
        Vector2 playerStart = default;
        await Server.WaitPost(() =>
        {
            vehicleStart = Transform.GetWorldPosition(vehicle);
            playerStart = Transform.GetWorldPosition(SPlayer);
        });

        // Cursor UID is not movement authority; the connected, unseated human moves instead.
        await PressKey(EngineKeyFunctions.MoveLeft, 15, cursorEntity: Target);
        await Server.WaitAssertion(() =>
        {
            Assert.That(Vector2.Distance(Transform.GetWorldPosition(vehicle), vehicleStart), Is.LessThan(0.01f));
            Assert.That(Transform.GetWorldPosition(SPlayer).X, Is.LessThan(playerStart.X - 0.05f));
        });
        // Let the unseated body's native friction settle before the move-sensitive entry.
        await Pair.RunSeconds(2);
        await Server.WaitPost(() => Transform.SetCoordinates(SPlayer, ToServer(PlayerCoords)));
        await RunTicks(5);

        await EnterThroughClient();
        var other = ToServer(await Spawn("MobHuman", PlayerCoords));
        await Server.WaitPost(() => Assert.That(vehicles.TryEnter(vehicle, other), Is.False,
            "A ContainerSlot must reject a second driver without changing ownership."));
        await Server.WaitAssertion(() => AssertDriver(vehicle, SPlayer));

        await PressKey(EngineKeyFunctions.MoveRight, 15, cursorEntity: Target);
        await Server.WaitAssertion(() =>
        {
            Assert.That(Transform.GetWorldPosition(vehicle).X, Is.GreaterThan(vehicleStart.X + 0.05f));
            AssertDriver(vehicle, SPlayer);
        });
        await Pair.RunUntilSynced();

        var verbs = Client.System<Content.Client.Verbs.VerbSystem>();
        await Client.WaitPost(() =>
        {
            var exit = verbs.GetLocalVerbs(CTarget!.Value, CPlayer, typeof(AlternativeVerb)).Single();
            verbs.ExecuteVerb(CTarget.Value, exit);
        });
        await RunTicks(10);
        await Server.WaitAssertion(() => AssertVacant(vehicle, SPlayer));
    }

    [Test]
    public async Task ConnectedNativeMoverDoesNotRetainDeletedRelativeGrid()
    {
        EntityUid referenceGrid = default;
        await Server.WaitPost(() =>
        {
            var grid = MapSystem.CreateGridEntity(MapId);
            referenceGrid = grid.Owner;
            Transform.SetCoordinates(referenceGrid, new EntityCoordinates(MapData.MapUid, new Vector2(0, 5)));
            for (var x = -1; x <= 2; x++)
            for (var y = -1; y <= 1; y++)
                MapSystem.SetTile(grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            Transform.SetCoordinates(SPlayer, new EntityCoordinates(referenceGrid, new Vector2(0.5f, 0.5f)));
        });
        await AddGravity(referenceGrid);
        // Let native movement adopt the new reference, then replicate it before testing its lifetime.
        await Pair.RunSeconds(InputMoverComponent.LerpTime + 0.25f);
        await Pair.RunUntilSynced();
        var clientReferenceGrid = ToClient(FromServer(referenceGrid));
        await Server.WaitAssertion(() =>
        {
            Assert.That(Xform(SPlayer).GridUid, Is.EqualTo(referenceGrid));
            Assert.That(Comp<InputMoverComponent>(Player).RelativeEntity, Is.EqualTo(referenceGrid));
            Assert.That(SEntMan.EntityExists(referenceGrid), Is.True);
        });
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.GetComponent<InputMoverComponent>(CPlayer).RelativeEntity, Is.EqualTo(clientReferenceGrid));
            Assert.That(CEntMan.EntityExists(clientReferenceGrid), Is.True);
        });

        Vector2 start = default;
        TimeSpan transitionDeadline = default;
        await Server.WaitPost(() => start = Transform.GetWorldPosition(SPlayer));
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down, cursorEntity: Player);
        try
        {
            await RunTicks(3);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Comp<InputMoverComponent>(Player).HeldMoveButtons & MoveButtons.Right, Is.EqualTo(MoveButtons.Right));
                Assert.That(Transform.GetWorldPosition(SPlayer).X, Is.GreaterThan(start.X));
            });
            await Server.WaitPost(() =>
            {
                Transform.SetCoordinates(SPlayer, new EntityCoordinates(MapData.Grid, new Vector2(-2.5f, 0.5f)));
                transitionDeadline = Comp<InputMoverComponent>(Player).LerpTarget;
            });
            await Server.WaitAssertion(() =>
            {
                Assert.That(Xform(SPlayer).MapUid, Is.EqualTo(MapData.MapUid));
                Assert.That(Xform(SPlayer).GridUid, Is.EqualTo(MapData.Grid.Owner));
                Assert.That(Comp<InputMoverComponent>(Player).LerpTarget, Is.GreaterThan(STiming.CurTime),
                    "Delete the old grid inside the native same-map transition's lerp window.");
            });
            await Server.WaitPost(() => SEntMan.DeleteEntity(referenceGrid));
            // Exercise real movement and ComponentGetState/ComponentHandleState, not a synthetic state request.
            await RunTicks(3);
            await Server.WaitAssertion(() =>
            {
                Assert.That(transitionDeadline, Is.GreaterThan(STiming.CurTime),
                    "A full lerp timeout must not hide a stale reference.");
                Assert.That(SEntMan.EntityExists(referenceGrid), Is.False);
                Assert.That(SEntMan.EntityExists(SPlayer), Is.True);
                Assert.That(SEntMan.GetComponent<MobStateComponent>(SPlayer).CurrentState, Is.EqualTo(MobState.Alive));
                Assert.That(ServerSession!.AttachedEntity, Is.EqualTo(SPlayer));
                var relative = Comp<InputMoverComponent>(Player).RelativeEntity;
                Assert.That(relative == null || SEntMan.EntityExists(relative.Value), Is.True,
                    "A surviving mover may reference a live entity or null, never the deleted grid.");
            });
            await Pair.RunUntilSynced();
            await Client.WaitAssertion(() =>
            {
                Assert.That(CEntMan.EntityExists(clientReferenceGrid), Is.False);
                Assert.That(CEntMan.EntityExists(CPlayer), Is.True);
                Assert.That(CEntMan.GetComponent<MobStateComponent>(CPlayer).CurrentState, Is.EqualTo(MobState.Alive));
                Assert.That(ClientSession.AttachedEntity, Is.EqualTo(CPlayer));
                var relative = CEntMan.GetComponent<InputMoverComponent>(CPlayer).RelativeEntity;
                Assert.That(relative == null || CEntMan.EntityExists(relative.Value), Is.True,
                    "Replicated/predicted movement must not retain the deleted grid either.");
            });
        }
        finally
        {
            await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up, cursorEntity: Player);
            await RunTicks(1);
        }
        await Pair.RunUntilSynced();
        Assert.That(Pair.ServerLogHandler.FailingLogs, Is.Empty);
        Assert.That(Pair.ClientLogHandler.FailingLogs, Is.Empty);
    }

    [Test]
    public async Task DisconnectWhileMovingVacatesNativeDriverSeatWithoutDeletingBody()
    {
        var vehicle = STarget!.Value;
        var original = SPlayer;
        var account = ServerSession!.UserId;
        var accountName = ServerSession.Name;
        await EnterThroughClient();
        Vector2 start = default;
        await Server.WaitPost(() => start = Transform.GetWorldPosition(vehicle));
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down, cursorEntity: Target);
        await RunTicks(15);
        await Server.WaitAssertion(() =>
        {
            Assert.That(Comp<InputMoverComponent>().HeldMoveButtons & MoveButtons.Right, Is.EqualTo(MoveButtons.Right));
            Assert.That(Transform.GetWorldPosition(vehicle).X, Is.GreaterThan(start.X + 0.05f));
        });

        try
        {
            await Client.WaitPost(() => Client.ResolveDependency<IClientConsoleHost>().ExecuteCommand("disconnect"));
            await RunTicks(10);
            await Task.WhenAll(Client.WaitIdleAsync(), Server.WaitIdleAsync());
            await Server.WaitAssertion(() =>
            {
                Assert.That(Server.ResolveDependency<IPlayerManager>().Sessions, Is.Empty);
                Assert.That(Comp<InputMoverComponent>().HeldMoveButtons, Is.EqualTo(MoveButtons.None),
                    "Disconnect must clear the held movement key on the vehicle.");
            });
            // Allow native friction to settle, then prove no continuing driverless motion.
            await Pair.RunSeconds(0.5f);
            await Server.WaitPost(() => start = Transform.GetWorldPosition(vehicle));
            await Pair.RunSeconds(0.5f);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Vector2.Distance(Transform.GetWorldPosition(vehicle), start), Is.LessThan(0.01f));
                AssertVacant(vehicle, original);
            });
        }
        finally
        {
            // Restore the actual connection even when the seat-vacancy assertion is RED.
            await Task.WhenAll(Client.WaitIdleAsync(), Server.WaitIdleAsync());
            Client.SetConnectTarget(Server);
            await Client.WaitPost(() => Client.ResolveDependency<IClientNetManager>().ClientConnect(null!, 0, accountName));
            await RunTicks(10);
            await Task.WhenAll(Client.WaitIdleAsync(), Server.WaitIdleAsync());
            await Server.WaitPost(() =>
            {
                var session = Server.ResolveDependency<IPlayerManager>().Sessions.Single();
                Assert.That(session.UserId, Is.EqualTo(account));
                Server.PlayerMan.SetAttachedEntity(session, original);
                Server.System<GameTicker>().PlayerJoinGame(session);
            });
            await RunTicks(10);
            await Client.WaitPost(() => ClientSession = Client.Session!);
            CPlayer = ToClient(Player);
            await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up, cursorEntity: Target);
            await RunTicks(5);
        }

        await Server.WaitPost(() =>
        {
            var session = Server.ResolveDependency<IPlayerManager>().Sessions.Single();
            Assert.That(session.UserId, Is.EqualTo(account));
            SPlayer = SEntMan.SpawnEntity(PlayerPrototype, Xform(vehicle).Coordinates.Offset(new Vector2(-1, 0)));
            Player = FromServer(SPlayer);
            Server.PlayerMan.SetAttachedEntity(session, SPlayer);
            Hands = SEntMan.GetComponentOrNull<Content.Shared.Hands.Components.HandsComponent>(SPlayer);
            DoAfters = SEntMan.GetComponentOrNull<Content.Shared.DoAfter.DoAfterComponent>(SPlayer);
        });
        await RunTicks(10);
        CPlayer = ToClient(Player);
        await Client.WaitPost(() => Client.ResolveDependency<IStateManager>().RequestStateChange<GameplayState>());
        // Use the exact vehicle's native verb; replacement drag-drop targeting remains unresolved.
        await Pair.RunUntilSynced();
        await Client.WaitPost(() =>
        {
            var verbs = Client.System<Content.Client.Verbs.VerbSystem>();
            var enter = verbs.GetLocalVerbs(CTarget!.Value, CPlayer, typeof(AlternativeVerb)).Single();
            Assert.That(enter.ClientExclusive, Is.False, "Entry must use the native network request.");
            verbs.ExecuteVerb(CTarget.Value, enter);
        });
        await RunTicks(3);
        await AwaitEntry();
        await Server.WaitAssertion(() =>
        {
            AssertDriver(vehicle, SPlayer);
            Assert.That(SEntMan.EntityExists(original), Is.True);
            Assert.That(SEntMan.GetComponent<MobStateComponent>(original).CurrentState, Is.EqualTo(MobState.Alive));
        });
    }

    [Test]
    public async Task DeathVacatesNativeDriverSeatWithoutDeletingCorpse()
    {
        var vehicle = STarget!.Value;
        var driver = SPlayer;
        await EnterThroughClient();
        await Server.WaitAssertion(() =>
        {
            AssertDriver(vehicle, driver);
            Assert.That(SEntMan.GetComponent<MobStateComponent>(driver).CurrentState, Is.EqualTo(MobState.Alive));
        });

        await Server.WaitPost(() => Server.System<MobStateSystem>().ChangeMobState(driver, MobState.Dead));
        await RunTicks(5);
        await Server.WaitAssertion(() => AssertVacant(vehicle, driver, MobState.Dead));
    }

    [Test]
    public async Task ConnectedAttachmentTransferKeepsNativeDriverSeatOccupied()
    {
        var vehicle = STarget!.Value;
        var driver = SPlayer;
        var session = ServerSession!;
        await EnterThroughClient();
        var other = ToServer(await Spawn("MobHuman", PlayerCoords));
        try
        {
            await Server.WaitPost(() => Server.PlayerMan.SetAttachedEntity(session, other));
            await RunTicks(5);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Server.ResolveDependency<IPlayerManager>().Sessions, Does.Contain(session));
                Assert.That(session.Status, Is.Not.EqualTo(SessionStatus.Disconnected));
                Assert.That(session.AttachedEntity, Is.EqualTo(other));
                Assert.That(SEntMan.EntityExists(driver), Is.True);
                Assert.That(SEntMan.GetComponent<MobStateComponent>(driver).CurrentState, Is.EqualTo(MobState.Alive));
                AssertDriver(vehicle, driver);
            });
        }
        finally
        {
            await Server.WaitPost(() => Server.PlayerMan.SetAttachedEntity(session, driver));
            await RunTicks(5);
        }
    }

    private async Task EnterThroughClient()
    {
        await DragDrop(Player, Target!.Value);
        await AwaitEntry();
    }

    private async Task AwaitEntry()
    {
        await Server.WaitAssertion(() =>
        {
            Assert.That(ActiveDoAfters.Count(), Is.EqualTo(1), "Client entry must start a nonzero entry DoAfter.");
            Assert.That(Comp<VehicleComponent>().Operator, Is.Null, "Entry must not complete immediately.");
        });
        // Bounded simulated time, not the unbounded AwaitDoAfters loop.
        await Pair.RunSeconds(1.25f);
        await Server.WaitAssertion(() =>
        {
            Assert.That(ActiveDoAfters, Is.Empty);
            AssertDriver(STarget!.Value, SPlayer);
        });
        await Pair.RunUntilSynced();
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.GetComponent<VehicleComponent>(CTarget!.Value).Operator, Is.EqualTo(CPlayer));
            Assert.That(CEntMan.GetComponent<RelayInputMoverComponent>(CPlayer).RelayEntity, Is.EqualTo(CTarget.Value));
        });
    }

    private void AssertDriver(EntityUid vehicle, EntityUid driver)
    {
        Assert.That(SEntMan.GetComponent<VehicleComponent>(vehicle).Operator, Is.EqualTo(driver));
        Assert.That(SEntMan.GetComponent<VehicleOperatorComponent>(driver).Vehicle, Is.EqualTo(vehicle));
        Assert.That(SEntMan.GetComponent<RelayInputMoverComponent>(driver).RelayEntity, Is.EqualTo(vehicle));
        Assert.That(SEntMan.GetComponent<MovementRelayTargetComponent>(vehicle).Source, Is.EqualTo(driver));
        Assert.That(Server.System<VehicleSystem>().TryGetOperatorContainer(vehicle, out var container), Is.True);
        Assert.That(container, Is.TypeOf<ContainerSlot>());
        Assert.That(container!.ContainedEntities, Is.EqualTo(new[] { driver }));
        Assert.That(HandSys.EnumerateHeld((driver, null)).Count(item =>
            SEntMan.TryGetComponent<VirtualItemComponent>(item, out var blocker) && blocker.BlockingEntity == vehicle), Is.EqualTo(1));
    }

    private void AssertVacant(EntityUid vehicle, EntityUid formerDriver, MobState expectedState = MobState.Alive)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SEntMan.EntityExists(formerDriver), Is.True, "Vacating must preserve the original physical body.");
            Assert.That(SEntMan.GetComponent<MobStateComponent>(formerDriver).CurrentState, Is.EqualTo(expectedState));
            Assert.That(SEntMan.GetComponent<VehicleComponent>(vehicle).Operator, Is.Null,
                "Vacating must release vehicle ownership, not merely stop input.");
            Assert.That(Server.System<VehicleSystem>().TryGetOperatorContainer(vehicle, out var container), Is.True);
            Assert.That(container, Is.TypeOf<ContainerSlot>());
            Assert.That(container!.ContainedEntities, Is.Empty, "The physical driver seat must be reusable.");
            Assert.That(SEntMan.HasComponent<VehicleOperatorComponent>(formerDriver), Is.False);
            Assert.That(SEntMan.HasComponent<RelayInputMoverComponent>(formerDriver), Is.False);
            Assert.That(SEntMan.HasComponent<InteractionRelayComponent>(formerDriver), Is.False);
            Assert.That(SEntMan.HasComponent<MovementRelayTargetComponent>(vehicle), Is.False);
            Assert.That(HandSys.EnumerateHeld((formerDriver, null)).Any(item =>
                SEntMan.TryGetComponent<VirtualItemComponent>(item, out var blocker) && blocker.BlockingEntity == vehicle), Is.False);
        }
    }
}
