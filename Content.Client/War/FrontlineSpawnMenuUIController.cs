using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Localization;

namespace Content.Client.War;

public sealed partial class FrontlineSpawnMenuUIController : UIController
{
    [Dependency] private IConfigurationManager _cfg = default!;

    private const string FrontlineCategory = "Frontline";
    private const string FilterControlName = "FrontlineSpawnModeFilter";
    private const int AllMode = 0;
    private const int FrontlineMode = 1;

    public override void Initialize()
    {
        base.Initialize();

        UIManager.WindowRoot.OnChildAdded += OnWindowAdded;

        foreach (var child in UIManager.WindowRoot.Children)
        {
            if (child is EntitySpawnWindow window)
                AttachFilter(window);
        }
    }

    private void OnWindowAdded(Control child)
    {
        if (child is EntitySpawnWindow window)
            AttachFilter(window);
    }

    private void AttachFilter(EntitySpawnWindow window)
    {
        if (window.GetChild(0) is not BoxContainer contents)
            return;

        foreach (var child in contents.Children)
        {
            if (child.Name == FilterControlName)
                return;
        }

        var row = new BoxContainer
        {
            Name = FilterControlName,
            Orientation = LayoutOrientation.Horizontal,
        };

        row.AddChild(new Label
        {
            Text = Loc.GetString("frontline-spawn-filter-label"),
            VerticalAlignment = VAlignment.Center,
        });

        var mode = new OptionButton
        {
            HorizontalExpand = true,
        };
        mode.AddItem(Loc.GetString("frontline-spawn-filter-all"), AllMode);
        mode.AddItem(Loc.GetString("frontline-spawn-filter-frontline"), FrontlineMode);

        var currentFilter = _cfg.GetCVar(CVars.EntitiesCategoryFilter);
        mode.SelectId(currentFilter == FrontlineCategory ? FrontlineMode : AllMode);

        mode.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            var category = args.Id == FrontlineMode ? FrontlineCategory : string.Empty;
            _cfg.SetCVar(CVars.EntitiesCategoryFilter, category);
            RefreshSpawnList(window);
        };

        row.AddChild(mode);
        contents.AddChild(row);
        row.SetPositionInParent(1);
    }

    private static void RefreshSpawnList(EntitySpawnWindow window)
    {
        // EntitySpawningUIController already owns list rebuilding and placement cleanup.
        // Nudge its existing SearchBar handler instead of duplicating spawn-menu internals here.
        var search = window.SearchBar.Text;
        window.SearchBar.SetText(search + " ", true);
        window.SearchBar.SetText(search, true);
    }
}
