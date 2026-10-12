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
        _window.Storage.Machine = Owner;
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
    public readonly FrontlineNativeStoragePanel Storage = new(null, false) { Name = "Products" };
    private readonly RichTextLabel _virtual = new() { Margin = new Robust.Shared.Maths.Thickness(6) };

    public FrontlineStockpileWindow()
    {
        IoCManager.InjectDependencies(this);
        Title = Loc.GetString("frontline-stockpile-title");
        MinSize = new Vector2(700, 320);
        SetSize = new Vector2(740, 480);
        var contents = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, VerticalExpand = true, SeparationOverride = 6 };
        contents.AddChild(new PanelContainer { StyleClasses = { "BackgroundDark" }, Children = { _virtual } });
        contents.AddChild(new Label { Text = Loc.GetString("frontline-ui-stockpile-heading"), StyleClasses = { "LabelHeading" } });
        contents.AddChild(Storage);
        ContentsContainer.AddChild(contents);
    }

    public void SetState(FrontlineStockpileUiState state, Func<ProtoId<FrontlineSupplyProductPrototype>, string> name)
    {
        _virtual.SetMessage(string.Join("\n", state.Products.Where(product => _prototypes.Index(product.Product).Entity == null)
            .Select(product => Loc.GetString("frontline-ui-virtual-supply", ("name", name(product.Product)), ("amount", product.Amount)))));
    }
}
