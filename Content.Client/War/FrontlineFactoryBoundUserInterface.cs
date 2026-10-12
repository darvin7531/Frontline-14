using System.Linq;
using System.Numerics;
using Content.Shared.Stacks;
using Content.Shared.War;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client.War;

[UsedImplicitly]
public sealed partial class FrontlineFactoryBoundUserInterface : BoundUserInterface
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private FrontlineFactoryWindow? _window;

    public FrontlineFactoryBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindowCenteredRight<FrontlineFactoryWindow>();
        _window.SetMachine(Owner);
        _window.Submit += recipe => SendMessage(new FrontlineFactorySubmitMessage(recipe, _window.Personal));
        _window.ModeChanged += personal => SendMessage(new FrontlineFactoryModeMessage(personal));
        _window.Cancel += id => SendMessage(new FrontlineFactoryCancelMessage(id, _window.Personal));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (_window?.Personal != true && state is FrontlineFactoryUiState factory)
            _window?.SetState(factory, StackName, EntityName, RecipeName);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is FrontlineFactoryViewMessage view && _window != null && view.Personal == _window.Personal)
            _window.SetState(view.State, StackName, EntityName, RecipeName);
    }

    private string StackName(ProtoId<StackPrototype> id)
    {
        return _prototypes.TryIndex(id, out var stack)
            ? Loc.GetString(stack.Name)
            : Loc.GetString("frontline-factory-item-unknown");
    }

    private string EntityName(EntProtoId id)
    {
        return _prototypes.TryIndex<EntityPrototype>(id, out var prototype)
            ? prototype.Name
            : Loc.GetString("frontline-factory-item-unknown");
    }

    private string RecipeName(ProtoId<FrontlineFactoryRecipePrototype> id)
    {
        return _prototypes.TryIndex(id, out var recipe) && !string.IsNullOrEmpty(recipe.Name)
            ? Loc.GetString(recipe.Name)
            : Loc.GetString("frontline-factory-recipe-unknown");
    }
}

public sealed partial class FrontlineFactoryWindow : DefaultWindow
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    public event Action<ProtoId<FrontlineFactoryRecipePrototype>>? Submit;
    private readonly FrontlineNativeStoragePanel _inputs;
    private readonly FrontlineItemGrid _recipes;
    private readonly FrontlineNativeStoragePanel _outputs;
    private readonly FrontlineJobList _jobs = new() { Name = "Queue" };
    private readonly RichTextLabel _details = new() { HorizontalExpand = true };
    private readonly Button _produce = new() { Name = "Produce", Disabled = true };
    private readonly Button _public = new() { Name = "Public", ToggleMode = true, Pressed = true, Text = "Public" };
    private readonly Button _personal = new() { Name = "Personal", ToggleMode = true, Text = "Personal" };
    public bool Personal => _personal.Pressed;
    public event Action<bool>? ModeChanged;
    public event Action<Guid>? Cancel;
    private FrontlineFactoryUiState _state = new([], [], []);
    private Func<ProtoId<StackPrototype>, string> _stackName = id => id.Id;
    private Func<ProtoId<FrontlineFactoryRecipePrototype>, string> _recipeName = id => id.Id;
    private Func<EntProtoId, string> _entityName = id => id.Id;

    public FrontlineFactoryWindow()
    {
        IoCManager.InjectDependencies(this);
        Title = Loc.GetString("frontline-factory-title");
        MinSize = new Vector2(600, 400);
        SetSize = new Vector2(740, 480);
        var columns = new FrontlineProductionColumns();
        var left = FrontlineItemGrid.Column(Loc.GetString("frontline-factory-input-heading"));
        var hint = new RichTextLabel();
        hint.SetMessage(Loc.GetString("frontline-ui-load-hint"));
        left.AddChild(hint);
        _inputs = new FrontlineNativeStoragePanel(null, false) { Name = "Inputs" };
        left.AddChild(_inputs);
        columns.AddChild(new PanelContainer { StyleClasses = { "BackgroundDark" }, HorizontalExpand = true, VerticalExpand = true, Children = { left } });
        var center = FrontlineItemGrid.Column(Loc.GetString("frontline-factory-recipes-heading"));
        _recipes = new FrontlineItemGrid(Loc.GetString("frontline-ui-recipes-empty"), true, mode: FrontlineItemGridMode.Recipes) { Name = "Recipes" };
        _recipes.SelectionChanged += UpdateSelection;
        center.AddChild(_recipes);
        var detailsScroll = FrontlineItemGrid.CreateScroll(_details);
        detailsScroll.MinHeight = 120;
        detailsScroll.MaxHeight = 160;
        detailsScroll.VerticalExpand = false;
        center.AddChild(detailsScroll);
        _produce.Text = Loc.GetString("frontline-ui-produce");
        _produce.OnPressed += _ =>
        {
            if (_recipes.SelectedId is { } id)
                Submit?.Invoke(new ProtoId<FrontlineFactoryRecipePrototype>(id));
        };
        center.AddChild(_produce);
        columns.AddChild(new PanelContainer { StyleClasses = { "BackgroundDark" }, HorizontalExpand = true, VerticalExpand = true, SizeFlagsStretchRatio = 1.4f, Children = { center } });
        var right = FrontlineItemGrid.Column(Loc.GetString("frontline-factory-output-heading"));
        _outputs = new FrontlineNativeStoragePanel(null, true) { Name = "Outputs" };
        right.AddChild(_outputs);
        right.AddChild(new PanelContainer { StyleClasses = { "LowDivider" } });
        right.AddChild(new Label { Text = Loc.GetString("frontline-factory-queue-heading"), StyleClasses = { "LabelHeading" } });
        right.AddChild(FrontlineItemGrid.CreateScroll(_jobs));
        columns.AddChild(new PanelContainer { StyleClasses = { "BackgroundDark" }, HorizontalExpand = true, VerticalExpand = true, Children = { right } });
        var mode = new BoxContainer();
        mode.AddChild(_public);
        mode.AddChild(_personal);
        _public.OnPressed += _ => SetMode(false);
        _personal.OnPressed += _ => SetMode(true);
        _jobs.Cancel += id => Cancel?.Invoke(id);
        var layout = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, VerticalExpand = true };
        layout.AddChild(mode);
        layout.AddChild(columns);
        ContentsContainer.AddChild(layout);
        UpdateSelection();
    }

    public void SetMachine(EntityUid machine)
    {
        _inputs.Machine = machine;
        _outputs.Machine = machine;
    }

    public void SetState(FrontlineFactoryUiState state,
        Func<ProtoId<StackPrototype>, string> stackName,
        Func<EntProtoId, string> entityName,
        Func<ProtoId<FrontlineFactoryRecipePrototype>, string> recipeName)
    {
        _state = state;
        _stackName = stackName;
        _recipeName = recipeName;
        _entityName = entityName;
        _recipes.SetItems(state.Recipes.Select(recipe => new FrontlineItemView(recipe.Id.Id,
            _recipes.EntityIcon(recipe.Output), recipe.OutputAmount, Details(recipe),
            _prototypes.Index(recipe.Id).Category, recipe.CanSubmit, Caption: Loc.GetString("frontline-ui-duration", ("seconds", Math.Ceiling(recipe.Duration.TotalSeconds))), Name: recipeName(recipe.Id))));
        _jobs.SetJobs(state.Jobs.Select(job =>
        {
            var recipe = state.Recipes.FirstOrDefault(recipe => recipe.Id == job.Recipe);
            return new FrontlineJobView(job.Recipe.Id, recipeName(job.Recipe), _recipes.EntityIcon(recipe?.Output),
                recipe?.Duration ?? TimeSpan.Zero, job.Remaining, job.Processing, ClaimId: job.Id, CanCancel: job.CanCancel);
        }));
        UpdateSelection();
    }

    private string Details(FrontlineFactoryRecipeState recipe) =>
        Loc.GetString("frontline-ui-recipe-details", ("name", _recipeName(recipe.Id)),
            ("input", string.Join("\n", recipe.Inputs.Select(input => $"{_stackName(input.Stack)} ×{input.Amount}"))),
            ("output", _entityName(recipe.Output)), ("amount", recipe.OutputAmount),
            ("seconds", Math.Ceiling(recipe.Duration.TotalSeconds)),
            ("availability", Loc.GetString(recipe.CanSubmit ? "frontline-ui-ready" : "frontline-ui-insufficient")));

    private void SetMode(bool personal)
    {
        _public.Pressed = !personal;
        _personal.Pressed = personal;
        ModeChanged?.Invoke(personal);
    }

    private void UpdateSelection()
    {
        var selected = _state.Recipes.FirstOrDefault(recipe => recipe.Id.Id == _recipes.SelectedId);
        _produce.Disabled = selected == null || !selected.CanSubmit;
        _details.SetMessage(selected == null ? Loc.GetString("frontline-ui-select-recipe") : Details(selected));
    }
}
