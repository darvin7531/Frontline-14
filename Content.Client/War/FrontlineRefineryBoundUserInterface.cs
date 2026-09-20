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
        _window.Submit += recipe => SendMessage(new FrontlineRefinerySubmitMessage(recipe));
        _window.Eject += () => SendMessage(new FrontlineRefineryEjectMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is FrontlineRefineryUiState refinery)
            _window?.SetState(refinery, StackName, RecipeName);
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

public sealed class FrontlineRefineryWindow : DefaultWindow
{
    public event Action<ProtoId<FrontlineRefineryRecipePrototype>>? Submit;
    public event Action? Eject;

    public FrontlineRefineryWindow()
    {
        Title = Loc.GetString("frontline-refinery-title");
        MinSize = new Vector2(420, 480);
    }

    public void SetState(
        FrontlineRefineryUiState state,
        Func<ProtoId<StackPrototype>, string> stackName,
        Func<ProtoId<FrontlineRefineryRecipePrototype>, string> recipeName)
    {
        ContentsContainer.RemoveAllChildren();
        var contents = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };

        contents.AddChild(new Label { Text = Loc.GetString("frontline-refinery-input-heading") });
        if (state.Inputs.Length == 0)
            contents.AddChild(new Label { Text = Loc.GetString("frontline-refinery-input-empty") });
        foreach (var input in state.Inputs)
        {
            contents.AddChild(new Label
            {
                Text = Loc.GetString("frontline-refinery-stack-line",
                    ("name", stackName(input.Stack)),
                    ("amount", input.Amount)),
            });
        }

        var eject = new Button { Text = Loc.GetString("frontline-refinery-eject-all") };
        eject.OnPressed += _ => Eject?.Invoke();
        contents.AddChild(eject);

        contents.AddChild(new Label { Text = Loc.GetString("frontline-refinery-recipes-heading") });
        foreach (var recipe in state.Recipes)
        {
            var inputs = string.Join(", ", recipe.Inputs.Select(input =>
                Loc.GetString("frontline-refinery-stack-amount",
                    ("name", stackName(input.Stack)),
                    ("amount", input.Amount))));
            var button = new Button
            {
                Disabled = !recipe.CanSubmit,
                Text = Loc.GetString("frontline-refinery-recipe-line",
                    ("input", inputs),
                    ("output", stackName(recipe.Output.Stack)),
                    ("amount", recipe.Output.Amount),
                    ("seconds", Math.Ceiling(recipe.Duration.TotalSeconds))).Replace(" → ", "\n→ "),
                HorizontalExpand = true,
            };
            var recipeId = recipe.Id;
            button.OnPressed += _ => Submit?.Invoke(recipeId);
            contents.AddChild(button);
        }

        contents.AddChild(new Label { Text = Loc.GetString("frontline-refinery-queue-heading") });
        if (state.Jobs.Length == 0)
            contents.AddChild(new Label { Text = Loc.GetString("frontline-refinery-queue-empty") });
        for (var i = 0; i < state.Jobs.Length; i++)
        {
            var job = state.Jobs[i];
            contents.AddChild(new Label
            {
                Text = Loc.GetString("frontline-refinery-job-line",
                    ("position", i + 1),
                    ("recipe", recipeName(job.Recipe)),
                    ("status", Loc.GetString(job.Processing
                        ? "frontline-refinery-status-processing"
                        : "frontline-refinery-status-waiting")),
                    ("seconds", Math.Max(0, Math.Ceiling(job.Remaining.TotalSeconds)))),
            });
        }

        var scroll = new ScrollContainer
        {
            HScrollEnabled = false,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        scroll.AddChild(contents);
        ContentsContainer.AddChild(scroll);
    }
}
