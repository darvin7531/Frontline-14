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
        _window.Submit += recipe => SendMessage(new FrontlineFactorySubmitMessage(recipe));
        _window.Eject += () => SendMessage(new FrontlineFactoryEjectMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is FrontlineFactoryUiState factory)
            _window?.SetState(factory, StackName, EntityName, RecipeName);
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

public sealed class FrontlineFactoryWindow : DefaultWindow
{
    public event Action<ProtoId<FrontlineFactoryRecipePrototype>>? Submit;
    public event Action? Eject;

    public FrontlineFactoryWindow()
    {
        Title = Loc.GetString("frontline-factory-title");
        MinSize = new Vector2(420, 480);
    }

    public void SetState(
        FrontlineFactoryUiState state,
        Func<ProtoId<StackPrototype>, string> stackName,
        Func<EntProtoId, string> entityName,
        Func<ProtoId<FrontlineFactoryRecipePrototype>, string> recipeName)
    {
        ContentsContainer.RemoveAllChildren();
        var contents = new BoxContainer { Orientation = LayoutOrientation.Vertical };

        contents.AddChild(new Label { Text = Loc.GetString("frontline-factory-input-heading") });
        if (state.Inputs.Length == 0)
            contents.AddChild(new Label { Text = Loc.GetString("frontline-factory-input-empty") });
        foreach (var input in state.Inputs)
        {
            contents.AddChild(new Label
            {
                Text = Loc.GetString("frontline-factory-stack-line",
                    ("name", stackName(input.Stack)),
                    ("amount", input.Amount)),
            });
        }

        var eject = new Button { Text = Loc.GetString("frontline-factory-eject-all") };
        eject.OnPressed += _ => Eject?.Invoke();
        contents.AddChild(eject);

        contents.AddChild(new Label { Text = Loc.GetString("frontline-factory-recipes-heading") });
        foreach (var recipe in state.Recipes)
        {
            var inputs = string.Join(", ", recipe.Inputs.Select(input =>
                Loc.GetString("frontline-factory-stack-amount",
                    ("name", stackName(input.Stack)),
                    ("amount", input.Amount))));
            var details = Loc.GetString("frontline-factory-recipe-line",
                ("recipe", recipeName(recipe.Id)),
                ("input", inputs),
                ("output", entityName(recipe.Output)),
                ("amount", recipe.OutputAmount),
                ("seconds", Math.Ceiling(recipe.Duration.TotalSeconds)));
            var button = new Button
            {
                Disabled = !recipe.CanSubmit,
                Text = Loc.GetString("frontline-factory-produce", ("recipe", details)),
            };
            var recipeId = recipe.Id;
            button.OnPressed += _ => Submit?.Invoke(recipeId);
            contents.AddChild(button);
        }

        contents.AddChild(new Label { Text = Loc.GetString("frontline-factory-queue-heading") });
        if (state.Jobs.Length == 0)
            contents.AddChild(new Label { Text = Loc.GetString("frontline-factory-queue-empty") });
        for (var i = 0; i < state.Jobs.Length; i++)
        {
            var job = state.Jobs[i];
            contents.AddChild(new Label
            {
                Text = Loc.GetString("frontline-factory-job-line",
                    ("position", i + 1),
                    ("recipe", recipeName(job.Recipe)),
                    ("status", Loc.GetString(job.Processing
                        ? "frontline-factory-status-processing"
                        : "frontline-factory-status-waiting")),
                    ("seconds", Math.Max(0, Math.Ceiling(job.Remaining.TotalSeconds)))),
            });
        }

        var scroll = new ScrollContainer();
        scroll.AddChild(contents);
        ContentsContainer.AddChild(scroll);
    }
}
