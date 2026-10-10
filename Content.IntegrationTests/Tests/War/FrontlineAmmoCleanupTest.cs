#nullable enable
using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Robust.Shared.GameObjects;
using Content.Shared.War;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineAmmoCleanupTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";
    private static readonly ProtoId<FrontlineSupplyProductPrototype> AmmoProduct = "MagazinePistol";

    [Test]
    public async Task RackingAllLiveRoundsRetainsAmmoAndMagazine()
    {
        var gunNet = await PlaceInHands("FrontlineWeaponPistolMk58");
        var gun = ToServer(gunNet);
        EntityUid magazine = default;
        await Server.WaitPost(() =>
        {
            magazine = SEntMan.SpawnEntity(Server.ProtoMan.Index(AmmoProduct).Entity,
                SEntMan.GetCoordinates(PlayerCoords));
            Assert.That(Server.System<ItemSlotsSystem>().TryInsert(gun, "gun_magazine", magazine, null), Is.True);
        });
        for (var rack = 0; rack < 11; rack++)
            await UseInHand();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(magazine), Is.True);
            Assert.That(SGun.GetAmmoCount(gun), Is.Zero);
            var cartridges = SEntMan.EntityQueryEnumerator<CartridgeAmmoComponent>();
            var live = 0;
            while (cartridges.MoveNext(out _, out var cartridge))
                if (!cartridge.Spent)
                    live++;
            Assert.That(live, Is.EqualTo(10), "Racking must eject usable rounds, not delete loaded ammunition.");
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task FinalFiredRoundCleansOnlyFrontlineAmmo(bool frontline)
    {
        await AddAtmosphere();
        var target = await SpawnTarget("MobHuman");
        var gunNet = await PlaceInHands(frontline ? "FrontlineWeaponPistolMk58" : "WeaponPistolMk58");
        var gun = ToServer(gunNet);
        EntityUid magazine = default;
        await Server.WaitPost(() =>
        {
            if (frontline)
            {
                magazine = SEntMan.SpawnEntity(Server.ProtoMan.Index(AmmoProduct).Entity,
                    SEntMan.GetCoordinates(PlayerCoords));
                Assert.That(Server.System<ItemSlotsSystem>().TryInsert(gun, "gun_magazine", magazine, null), Is.True);
            }
            else
                magazine = Server.System<ItemSlotsSystem>().GetItemOrNull(gun, "gun_magazine")!.Value;
        });
        // Chambering the last cartridge is not firing it: retain the magazine through shot nine.
        await UseInHand();
        await Pair.RunSeconds(2f);
        await SetCombatMode(true);
        var damage = Server.System<DamageableSystem>();
        var victim = (ToServer(target), SComp<DamageableComponent>(ToServer(target)));
        for (var shot = 0; shot < 10; shot++)
        {
            var before = damage.GetPositiveDamage(victim).GetTotal();
            await Server.WaitPost(() =>
            {
                Assert.That(SGun.TryGetGun(SPlayer, out var held), Is.True);
                Assert.That(SGun.AttemptShoot(SPlayer, held, Position(target), ToServer(target)), Is.True);
            });
            await Pair.RunSeconds(0.4f);
            await Server.WaitAssertion(() =>
            {
                Assert.That(damage.GetPositiveDamage(victim).GetTotal(), Is.GreaterThan(before),
                    "Every round, including the final round, still damages its target.");
                Assert.That(SGun.GetAmmoCount(gun), Is.EqualTo(9 - shot));
                Assert.That(SEntMan.EntityExists(magazine), Is.EqualTo(!frontline || shot < 9),
                    "Delete only after the actual final shot, not the last chamber refill.");
                var spent = SEntMan.EntityQueryEnumerator<CartridgeAmmoComponent>();
                var spentCount = 0;
                while (spent.MoveNext(out _, out var cartridge))
                    if (cartridge.Spent)
                        spentCount++;
                Assert.That(spentCount, frontline ? Is.Zero : Is.GreaterThan(0));
            });
        }
        if (!frontline)
            return;
        await Server.WaitPost(() =>
        {
            Assert.That(Server.System<ItemSlotsSystem>().GetItemOrNull(gun, "gun_magazine"), Is.Null);
            var reload = SEntMan.SpawnEntity(Server.ProtoMan.Index(AmmoProduct).Entity,
                SEntMan.GetCoordinates(PlayerCoords));
            Assert.That(Server.System<ItemSlotsSystem>().TryInsert(gun, "gun_magazine", reload, null), Is.True);
        });
        await SetCombatMode(false);
        await UseInHand();
        await Pair.RunSeconds(0.4f);
        await SetCombatMode(true);
        await Server.WaitPost(() =>
        {
            Assert.That(SGun.TryGetGun(SPlayer, out var held), Is.True);
            Assert.That(SGun.AttemptShoot(SPlayer, held, Position(target), ToServer(target)), Is.True);
        });
        await RunTicks(2);
        await Server.WaitAssertion(() => Assert.That(SGun.GetAmmoCount(gun), Is.EqualTo(9)));
    }
}
