using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.War;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.War;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed partial class FrontlineWorkTest : GameTest
{
    [Test]
    public async Task NativeToolRepairIsBlockedByMiningWithoutBlockingOtherToolUse()
    {
        var map = await Pair.CreateTestMap();
        var fields = Server.System<FrontlineResourceFieldSystem>();
        var tools = Server.System<Content.Shared.Tools.Systems.SharedToolSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var doAfter = Server.System<SharedDoAfterSystem>();
        await Server.WaitPost(() =>
        {
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            SEntMan.AddComponent<FrontlinePlayerComponent>(user);
            var pickaxe = SEntMan.SpawnEntity("Pickaxe", map.GridCoords);
            var crowbar = SEntMan.SpawnEntity("Crowbar", map.GridCoords);
            hands.TryPickupAnyHand(user, pickaxe);
            hands.TryPickupAnyHand(user, crowbar);
            var node = SEntMan.SpawnEntity("FrontlineIronResourceNode", map.GridCoords);
            var target = SEntMan.SpawnEntity(null, map.GridCoords);
            Assert.That(fields.TryStartExtraction(node, user, pickaxe), Is.True);
            Robust.Shared.Prototypes.ProtoId<Content.Shared.Tools.ToolQualityPrototype> prying = "Prying";
            tools.UseTool(crowbar, user, target, System.TimeSpan.FromSeconds(1), new[] { prying },
                new Content.Shared.Repairable.RepairDoAfterEvent(), out var repair);
            Assert.That(repair, Is.Null);
            var mining = SEntMan.GetComponent<DoAfterComponent>(user).DoAfters.Values.Single();
            Assert.That(mining.Cancelled, Is.False);
            tools.UseTool(crowbar, user, target, System.TimeSpan.FromSeconds(1), new[] { prying },
                new Content.Shared.Anomaly.ScannerDoAfterEvent(), out var unrelated);
            Assert.That(unrelated, Is.Not.Null);
            doAfter.Cancel(unrelated);
            doAfter.Cancel(user, mining.Index);
            tools.UseTool(crowbar, user, target, System.TimeSpan.FromSeconds(1), new[] { prying },
                new Content.Shared.Repairable.RepairDoAfterEvent(), out repair);
            Assert.That(repair, Is.Not.Null);
            doAfter.Cancel(repair);
        });
    }


    [TestCase(true)]
    [TestCase(false)]
    public async Task ActorCannotMineWithTwoToolsAndCanRetryAfterCancellation(bool frontline)
    {
        var map = await Pair.CreateTestMap();
        var fields = Server.System<FrontlineResourceFieldSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var doAfter = Server.System<SharedDoAfterSystem>();
        await Server.WaitPost(() =>
        {
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            SEntMan.AddComponent<FrontlinePlayerComponent>(user);
            var first = SEntMan.SpawnEntity("Pickaxe", map.GridCoords);
            var second = SEntMan.SpawnEntity("Pickaxe", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, first), Is.True);
            Assert.That(hands.TryPickupAnyHand(user, second), Is.True);
            var a = SEntMan.SpawnEntity("FrontlineIronResourceNode", map.GridCoords);
            var b = SEntMan.SpawnEntity("FrontlineIronResourceNode", map.GridCoords);
            Assert.That(fields.TryStartExtraction(a, user, first), Is.True);
            if (!frontline)
                SEntMan.RemoveComponent<FrontlinePlayerComponent>(user);
            Assert.That(fields.TryStartExtraction(b, user, second), Is.EqualTo(!frontline));
            if (!frontline)
            {
                foreach (var action in SEntMan.GetComponent<DoAfterComponent>(user).DoAfters.Values.ToArray())
                    doAfter.Cancel(user, action.Index);
                return;
            }
            var pending = SEntMan.GetComponent<DoAfterComponent>(user).DoAfters.Values.Single();
            Assert.That(pending.Cancelled, Is.False);
            doAfter.Cancel(user, pending.Index);
            Assert.That(fields.TryStartExtraction(b, user, second), Is.True);
        });
    }
}
