using System.Linq;
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

public sealed partial class FrontlineStockpileWindow : DefaultWindow
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    public event Action? Submit;
    public event Action<ProtoId<FrontlineSupplyProductPrototype>>? Withdraw;
    private readonly FrontlineItemGrid _products;
    private readonly RichTextLabel _virtual = new() { Margin = new Robust.Shared.Maths.Thickness(6) };
    private readonly Button _withdraw = new() { Name = "Withdraw", Disabled = true };
    private FrontlineStockpileUiState _state = new([]);
    private Func<ProtoId<FrontlineSupplyProductPrototype>, string> _name = id => id.Id;

    public FrontlineStockpileWindow()
    {
        IoCManager.InjectDependencies(this);
        Title = Loc.GetString("frontline-stockpile-title");
        MinSize = new Vector2(360, 320);
        SetSize = new Vector2(480, 480);
        var contents = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, VerticalExpand = true, SeparationOverride = 6 };
        var submit = new Button { Name = "Deposit", Text = Loc.GetString("frontline-stockpile-submit") };
        submit.OnPressed += _ => Submit?.Invoke();
        contents.AddChild(submit);
        contents.AddChild(new PanelContainer { StyleClasses = { "BackgroundDark" }, Children = { _virtual } });
        contents.AddChild(new Label { Text = Loc.GetString("frontline-ui-stockpile-heading"), StyleClasses = { "LabelHeading" } });
        contents.AddChild(new PanelContainer { StyleClasses = { "LowDivider" } });
        _products = new FrontlineItemGrid(Loc.GetString("frontline-ui-stockpile-empty"), true) { Name = "Products" };
        _products.SelectionChanged += UpdateSelection;
        contents.AddChild(_products);
        _withdraw.OnPressed += _ =>
        {
            if (_products.SelectedId is { } id)
                Withdraw?.Invoke(new ProtoId<FrontlineSupplyProductPrototype>(id));
        };
        contents.AddChild(_withdraw);
        ContentsContainer.AddChild(contents);
        UpdateSelection();
    }

    public void SetState(FrontlineStockpileUiState state, Func<ProtoId<FrontlineSupplyProductPrototype>, string> name)
    {
        _state = state;
        _name = name;
        _products.SetItems(state.Products.Where(product => product.Amount > 0 && _prototypes.Index(product.Product).Entity != null)
            .Select(product => new FrontlineItemView(product.Product.Id,
                _products.EntityIcon(_prototypes.Index(product.Product).Entity), product.Amount,
                Loc.GetString("frontline-stockpile-count", ("name", name(product.Product)), ("amount", product.Amount)),
                _prototypes.Index(product.Product).Category, Name: name(product.Product))));
        _virtual.SetMessage(string.Join("\n", state.Products.Where(product => _prototypes.Index(product.Product).Entity == null)
            .Select(product => Loc.GetString("frontline-ui-virtual-supply", ("name", name(product.Product)), ("amount", product.Amount)))));
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _state.Products.FirstOrDefault(product => product.Product.Id == _products.SelectedId);
        _withdraw.Disabled = selected == null || !selected.CanWithdraw;
        _withdraw.Text = selected == null ? Loc.GetString("frontline-ui-select-product") :
            Loc.GetString("frontline-ui-withdraw");
        _withdraw.ToolTip = selected == null ? null : _name(selected.Product);
    }
}
