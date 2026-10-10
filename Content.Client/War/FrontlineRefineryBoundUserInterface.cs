using System.Linq;
using System.Numerics;
using Content.Shared.Stacks;
using Content.Shared.War;
using JetBrains.Annotations;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client.War;

[UsedImplicitly]
public sealed partial class FrontlineRefineryBoundUserInterface : BoundUserInterface
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private FrontlineRefineryWindow? _window;

    public FrontlineRefineryBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindowCenteredRight<FrontlineRefineryWindow>();
        _window.Submit += (recipe, batches, all) => SendMessage(new FrontlineRefinerySubmitMessage(recipe, batches, all, _window.Personal));
        _window.Eject += () => SendMessage(new FrontlineRefineryEjectMessage());
        _window.TakeOutput += () => SendMessage(new FrontlineRefineryTakeOutputMessage(_window.Personal));
        _window.ModeChanged += personal => SendMessage(new FrontlineRefineryModeMessage(personal));
        _window.Cancel += id => SendMessage(new FrontlineRefineryCancelMessage(id, _window.Personal));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (_window?.Personal != true && state is FrontlineRefineryUiState refinery)
            _window?.SetState(refinery, StackName, RecipeName);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is FrontlineRefineryViewMessage view && _window != null && view.Personal == _window.Personal)
            _window.SetState(view.State, StackName, RecipeName);
    }

    private string StackName(ProtoId<StackPrototype> id)
    {
        return _prototypes.TryIndex(id, out var stack) ? Loc.GetString(stack.Name) : id.Id;
    }

    private string RecipeName(ProtoId<FrontlineRefineryRecipePrototype> id)
    {
        return _prototypes.TryIndex(id, out var recipe) && !string.IsNullOrEmpty(recipe.Name)
            ? Loc.GetString(recipe.Name)
            : Loc.GetString("frontline-refinery-recipe-unknown");
    }
}

public sealed partial class FrontlineRefineryWindow : DefaultWindow
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IInputManager _input = default!;
    public event Action<ProtoId<FrontlineRefineryRecipePrototype>, int, bool>? Submit;
    public event Action? Eject;
    public event Action? TakeOutput;
    private readonly FrontlineItemGrid _inputs;
    private readonly FrontlineItemGrid _recipes;
    private readonly FrontlineItemGrid _outputs;
    private readonly FrontlineJobList _jobs = new() { Name = "Queue" };
    private readonly RichTextLabel _details = new() { HorizontalExpand = true };
    private readonly SpinBox _batches = new() { Name = "Batches", HorizontalExpand = true };
    private int _maximumBatches = 1;
    private readonly Button _produce = new() { Name = "Produce", Disabled = true };
    private readonly Button _eject = new() { Name = "Eject" };
    private readonly Button _take = new() { Name = "TakeOutput" };
    private readonly Button _public = new() { Name = "Public", ToggleMode = true, Pressed = true, Text = "Public" };
    private readonly Button _personal = new() { Name = "Personal", ToggleMode = true, Text = "Personal" };
    public bool Personal => _personal.Pressed;
    public event Action<bool>? ModeChanged;
    public event Action<Guid>? Cancel;
    private FrontlineRefineryUiState _state = new([], [], []);
    private Func<ProtoId<StackPrototype>, string> _stackName = id => id.Id;
    private Func<ProtoId<FrontlineRefineryRecipePrototype>, string> _recipeName = id => id.Id;

    public FrontlineRefineryWindow()
    {
        IoCManager.InjectDependencies(this);
        Title = Loc.GetString("frontline-refinery-title");
        MinSize = new Vector2(600, 400);
        SetSize = new Vector2(740, 480);
        var columns = new BoxContainer { HorizontalExpand = true, VerticalExpand = true, SeparationOverride = 8 };
        var left = FrontlineItemGrid.Column(Loc.GetString("frontline-refinery-input-heading"));
        var hint = new RichTextLabel();
        hint.SetMessage(Loc.GetString("frontline-ui-load-hint"));
        left.AddChild(hint);
        _inputs = new FrontlineItemGrid(Loc.GetString("frontline-refinery-input-empty"), mode: FrontlineItemGridMode.Slots) { Name = "Inputs" };
        left.AddChild(_inputs);
        _eject.Text = Loc.GetString("frontline-refinery-eject-all");
        _eject.OnPressed += _ => Eject?.Invoke();
        left.AddChild(_eject);
        columns.AddChild(new PanelContainer { StyleClasses = { "BackgroundDark" }, HorizontalExpand = true, VerticalExpand = true, Children = { left } });
        var center = FrontlineItemGrid.Column(Loc.GetString("frontline-refinery-recipes-heading"));
        _recipes = new FrontlineItemGrid(Loc.GetString("frontline-ui-recipes-empty"), mode: FrontlineItemGridMode.Recipes) { Name = "Recipes" };
        _recipes.SelectionChanged += UpdateSelection;
        center.AddChild(_recipes);
        var detailsScroll = FrontlineItemGrid.CreateScroll(_details);
        detailsScroll.MinHeight = 120;
        detailsScroll.MaxHeight = 160;
        detailsScroll.VerticalExpand = false;
        center.AddChild(detailsScroll);
        center.AddChild(new Label { Text = Loc.GetString("frontline-refinery-batches") });
        _batches.IsValid = value => value >= 1 && value <= _maximumBatches;
        _batches.Value = 1;
        _batches.InitDefaultButtons();
        center.AddChild(_batches);
        var batchHint = new RichTextLabel();
        batchHint.SetMessage(Loc.GetString("frontline-refinery-batch-hint"));
        var hintScroll = FrontlineItemGrid.CreateScroll(batchHint);
        hintScroll.MinHeight = 40;
        hintScroll.MaxHeight = 60;
        hintScroll.VerticalExpand = false;
        center.AddChild(hintScroll);
        _produce.Text = Loc.GetString("frontline-ui-produce");
        _produce.OnPressed += _ =>
        {
            if (_recipes.SelectedId is { } id)
                Submit?.Invoke(new ProtoId<FrontlineRefineryRecipePrototype>(id), _batches.Value, _input.IsKeyDown(Keyboard.Key.Shift));
        };
        center.AddChild(_produce);
        columns.AddChild(new PanelContainer { StyleClasses = { "BackgroundDark" }, HorizontalExpand = true, VerticalExpand = true, SizeFlagsStretchRatio = 1.4f, Children = { center } });
        var right = FrontlineItemGrid.Column(Loc.GetString("frontline-refinery-output-heading"));
        _outputs = new FrontlineItemGrid(Loc.GetString("frontline-refinery-output-empty"), mode: FrontlineItemGridMode.Slots) { Name = "Outputs" };
        right.AddChild(_outputs);
        _take.Text = Loc.GetString("frontline-refinery-take-output");
        _take.OnPressed += _ => TakeOutput?.Invoke();
        right.AddChild(_take);
        right.AddChild(new PanelContainer { StyleClasses = { "LowDivider" } });
        right.AddChild(new Label { Text = Loc.GetString("frontline-refinery-queue-heading"), StyleClasses = { "LabelHeading" } });
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

    public void SetState(FrontlineRefineryUiState state,
        Func<ProtoId<StackPrototype>, string> stackName,
        Func<ProtoId<FrontlineRefineryRecipePrototype>, string> recipeName)
    {
        _state = state;
        _stackName = stackName;
        _recipeName = recipeName;
        _inputs.SetItems(state.Inputs.Select(input => new FrontlineItemView(input.Stack.Id,
            _inputs.StackIcon(input.Stack), input.Amount, $"{stackName(input.Stack)} ×{input.Amount}", Name: stackName(input.Stack))));
        _outputs.SetItems(state.Outputs.Select(output => new FrontlineItemView(output.Stack.Id,
            _outputs.StackIcon(output.Stack), output.Amount, $"{stackName(output.Stack)} ×{output.Amount}", Name: stackName(output.Stack))));
        _recipes.SetItems(state.Recipes.Select(recipe => new FrontlineItemView(recipe.Id.Id,
            _recipes.StackIcon(recipe.Output.Stack), recipe.Output.Amount, Details(recipe),
            Available: recipe.CanSubmit, Caption: Loc.GetString("frontline-ui-duration", ("seconds", Math.Ceiling(recipe.Duration.TotalSeconds))), Name: recipeName(recipe.Id))));
        _jobs.SetJobs(state.Jobs.Select(job =>
        {
            var recipe = state.Recipes.FirstOrDefault(recipe => recipe.Id == job.Recipe);
            return new FrontlineJobView(job.Recipe.Id, recipeName(job.Recipe),
                recipe == null ? null : _recipes.StackIcon(recipe.Output.Stack),
                recipe?.Duration ?? TimeSpan.Zero, job.Remaining, job.Processing, job.Batches, job.Id, job.CanCancel);
        }));
        _eject.Disabled = state.Inputs.Length == 0;
        _take.Disabled = state.OutputStackCount == 0;
        UpdateSelection();
    }

    private string Details(FrontlineRefineryRecipeState recipe) =>
        Loc.GetString("frontline-ui-recipe-details", ("name", _recipeName(recipe.Id)),
            ("input", string.Join("\n", recipe.Inputs.Select(input => $"{_stackName(input.Stack)} ×{input.Amount}"))),
            ("output", _stackName(recipe.Output.Stack)), ("amount", recipe.Output.Amount),
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
        var available = selected == null ? 0 : selected.Inputs.Min(required =>
            _state.Inputs.Where(input => input.Stack == required.Stack).Sum(input => (long) input.Amount) / required.Amount);
        var previousMaximum = _maximumBatches;
        _maximumBatches = (int) Math.Clamp(available, 1, int.MaxValue);
        var quantity = Math.Clamp(_batches.Value, 1, _maximumBatches);
        if (_batches.Value != quantity || previousMaximum != _maximumBatches)
            _batches.OverrideValue(quantity);
        _produce.Disabled = selected == null || !selected.CanSubmit;
        _details.SetMessage(selected == null ? Loc.GetString("frontline-ui-select-recipe") : Details(selected));
    }
}
