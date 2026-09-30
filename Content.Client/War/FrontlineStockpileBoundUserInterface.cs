using System.Numerics;
using Content.Shared.War;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;

namespace Content.Client.War;

[UsedImplicitly]
public sealed partial class FrontlineStockpileBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    private FrontlineStockpileWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindowCenteredRight<FrontlineStockpileWindow>();
        _window.Submit += () => SendMessage(new FrontlineStockpileSubmitMessage());
        _window.Withdraw += product => SendMessage(new FrontlineStockpileWithdrawMessage(product));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is FrontlineStockpileUiState stockpile)
            _window?.SetState(stockpile, id => Loc.GetString(_prototypes.Index(id).Name));
    }
}

public sealed class FrontlineStockpileWindow : DefaultWindow
{
    public event Action? Submit;
    public event Action<ProtoId<FrontlineSupplyProductPrototype>>? Withdraw;

    public FrontlineStockpileWindow()
    {
        Title = Loc.GetString("frontline-stockpile-title");
        MinSize = new Vector2(360, 320);
    }

    public void SetState(FrontlineStockpileUiState state, Func<ProtoId<FrontlineSupplyProductPrototype>, string> name)
    {
        ContentsContainer.RemoveAllChildren();
        var contents = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        var submit = new Button { Text = Loc.GetString("frontline-stockpile-submit") };
        submit.OnPressed += _ => Submit?.Invoke();
        contents.AddChild(submit);
        foreach (var product in state.Products)
        {
            contents.AddChild(new Label
            {
                Text = Loc.GetString("frontline-stockpile-count", ("name", name(product.Product)), ("amount", product.Amount)),
            });
            var button = new Button
            {
                Text = Loc.GetString("frontline-stockpile-withdraw", ("name", name(product.Product))),
                Disabled = !product.CanWithdraw,
            };
            button.OnPressed += _ => Withdraw?.Invoke(product.Product);
            contents.AddChild(button);
        }
        var scroll = new ScrollContainer { HScrollEnabled = false, VerticalExpand = true, HorizontalExpand = true };
        scroll.AddChild(contents);
        ContentsContainer.AddChild(scroll);
    }
}
