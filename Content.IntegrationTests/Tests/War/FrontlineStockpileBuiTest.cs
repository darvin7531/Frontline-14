#nullable enable
using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.War;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using System.Numerics;
using Content.Shared.War;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineStockpileBuiTest : InteractionTest
{
    [TestCase("FrontlineFactionOne")]
    [TestCase("FrontlineFactionTwo")]
    public async Task RealRepairBuildsUsableEmptyStockpileWhileRuinHasNone(string faction)
    {
        await SelectFaction(faction);
        await SpawnTarget("TownHallRuin");
        await Server.WaitPost(() =>
        {
            Assert.That(SEntMan.HasComponent<FrontlineStockpileComponent>(STarget!.Value), Is.False);
            var marker = SEntMan.SpawnEntity(null, MapData.GridCoords);
            SEntMan.AddComponent<TerritoryComponent>(marker).Configure(new TerritoryId("stockpile-repair"), new Vector2(-2), new Vector2(2));
            SComp<TownHallRuinComponent>(STarget.Value).TerritoryId = "stockpile-repair";
            SComp<TownHallRuinComponent>(STarget.Value).RequiredBasicMaterials = 1;
        });
        await InteractUsing("BasicMaterials1");
        await Pair.RunSeconds(2.5f);
        await Server.WaitPost(() =>
        {
            var hall = SEntMan.EntityQuery<TownHallComponent, MetaDataComponent>()
                .Single(pair => pair.Item1.TerritoryId == "stockpile-repair");
            Target = SEntMan.GetNetEntity(hall.Item2.Owner);
            Assert.That(hall.Item1.FactionId, Is.EqualTo(faction));
            Assert.That(SComp<FrontlineStockpileComponent>(hall.Item2.Owner).Counts, Is.Empty);
        });
        await RunTicks(15);
        await Interact();
        Assert.That(TryGetBui(FrontlineStockpileUiKey.Key, out _), Is.True);
        await Server.WaitPost(() =>
        {
            var crate = SEntMan.SpawnEntity("FrontlineSupplyCrate", SEntMan.GetCoordinates(PlayerCoords));
            Assert.That(HandSys.TryPickupAnyHand(SPlayer, crate), Is.True);
        });
        await SendBui(FrontlineStockpileUiKey.Key, new FrontlineStockpileSubmitMessage());
        await Server.WaitAssertion(() => Assert.That(SComp<FrontlineStockpileComponent>(STarget!.Value)
            .Counts["SoldierSupplies"], Is.EqualTo(5)));
        await CloseBui(FrontlineStockpileUiKey.Key);
    }

    private bool _wroteWar;

    public override async Task DoTeardown()
    {
        if (_wroteWar)
            await Server.WaitPost(() =>
            {
                var resources = Server.ResolveDependency<IResourceManager>();
                resources.UserData.Delete(WarStateSystem.SavePath);
                resources.UserData.Delete(WarFactionSystem.SavePath);
            });
        await base.DoTeardown();
    }

    private async Task SelectFaction(string faction)
    {
        await Server.WaitPost(() =>
        {
            _wroteWar = true;
            Server.System<WarStateSystem>().StartNewWar();
            Assert.That(Server.System<WarFactionSystem>().SetFaction(Pair.Player!.UserId, new FactionId(faction)), Is.True);
            Assert.That(Server.System<WarFactionSystem>().TryGetFaction(Pair.Player.UserId, out var selected), Is.True);
            Assert.That(selected.Id, Is.EqualTo(faction));
        });
    }

    [TestCase("FrontlineFactionOne", "TownHallCoreFactionTwo")]
    [TestCase("FrontlineFactionTwo", "TownHallCoreFactionOne")]
    public async Task ConnectedClientReceivesInitialStateAndSubmitsWithdrawsThroughBui(string faction, string core)
    {
        await SelectFaction(faction);
        await SpawnTarget(core);
        await Interact();
        Assert.That(TryGetBui(FrontlineStockpileUiKey.Key, out var bui), Is.True);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CUiSys.TryGetUiState<FrontlineStockpileUiState>(CTarget!.Value, FrontlineStockpileUiKey.Key, out var state), Is.True);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.Products.Single(p => p.Product.Id == "FrontlineWeaponPistolMk58").Amount, Is.Zero);
        });
        EntityUid crate = default;
        await Server.WaitPost(() =>
        {
            crate = SEntMan.SpawnEntity("FrontlineWeaponCrate", SEntMan.GetCoordinates(PlayerCoords));
            Assert.That(HandSys.TryPickupAnyHand(SPlayer, crate), Is.True);
        });
        await SendBui(FrontlineStockpileUiKey.Key, new FrontlineStockpileSubmitMessage());
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(crate), Is.False);
            Assert.That(SComp<FrontlineStockpileComponent>(STarget!.Value).Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(5));
        });
        await SendBui(FrontlineStockpileUiKey.Key, new FrontlineStockpileWithdrawMessage("FrontlineWeaponPistolMk58"));
        await Client.WaitAssertion(() =>
        {
            Assert.That(CUiSys.TryGetUiState<FrontlineStockpileUiState>(CTarget!.Value, FrontlineStockpileUiKey.Key, out var state), Is.True);
            Assert.That(state!.Products.Single(p => p.Product.Id == "FrontlineWeaponPistolMk58").Amount, Is.EqualTo(4));
        });
        await Server.WaitAssertion(() => Assert.That(SEntMan.EntityQuery<MetaDataComponent>()
            .Count(m => m.EntityPrototype?.ID == "FrontlineWeaponPistolMk58"), Is.EqualTo(1)));
        await SendBui(FrontlineStockpileUiKey.Key, new FrontlineStockpileSubmitMessage());
        await SendBui(FrontlineStockpileUiKey.Key, new FrontlineStockpileWithdrawMessage("UnknownStockpileProduct"));
        await Server.WaitAssertion(() => Assert.That(SComp<FrontlineStockpileComponent>(STarget!.Value)
            .Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(4)));
        await CloseBui(FrontlineStockpileUiKey.Key);
    }
}
