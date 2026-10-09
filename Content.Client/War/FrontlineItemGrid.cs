using System.Linq;
using System.Numerics;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.War;

public readonly record struct FrontlineItemView(
    string Id, Texture? Icon, int Amount, string Tooltip,
    string Category = "frontline-ui-category-other", bool Available = true, Texture? InputIcon = null, int InputAmount = 0, string Caption = "", string Name = "");

/// <summary>Prototype icons only: never spawn a client entity to render an item.</summary>
public sealed partial class FrontlineItemGrid : BoxContainer
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IEntityManager _entities = default!;
    public readonly OptionButton Categories = new() { Name = "Categories" };
    public readonly ScrollContainer Scroll;
    public readonly GridContainer Cells;
    private readonly RichTextLabel _empty = new();
    private readonly Dictionary<string, (ContainerButton Button, TextureRect Icon, Label Count, Label Fallback, RichTextLabel Caption, RichTextLabel Name)> _cells = new();
    private readonly Dictionary<string, FrontlineItemView> _items = new();
    private string[] _categories = [];
    public string? SelectedId { get; private set; }
    public string SelectedCategory { get; private set; } = "";
    public event Action? SelectionChanged;

    public FrontlineItemGrid(string empty, bool categories = false)
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        Cells = new ResponsiveGrid(164) { HSeparationOverride = 8, VSeparationOverride = 8 };
        HorizontalExpand = VerticalExpand = true;
        MinWidth = 156;
        Categories.Visible = categories;
        Categories.OnItemSelected += args =>
        {
            SelectedCategory = _categories[args.Id];
            Categories.SelectId(args.Id);
            ApplyFilter();
        };
        AddChild(Categories);
        _empty.SetMessage(empty);
        var body = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true };
        body.AddChild(_empty);
        body.AddChild(Cells);
        Scroll = CreateScroll(body);
        AddChild(Scroll);
    }

    public Texture? EntityIcon(EntProtoId? id)
    {
        return id is { } entity && !string.IsNullOrWhiteSpace(entity.Id) &&
               _prototypes.TryIndex(entity, out var prototype)
            ? _entities.System<SpriteSystem>().GetPrototypeIcon(prototype).Default
            : null;
    }

    public Texture? StackIcon(ProtoId<StackPrototype> id) =>
        _prototypes.TryIndex(id, out var stack) ? EntityIcon(stack.Spawn) : null;

    public void SetItems(IEnumerable<FrontlineItemView> items)
    {
        var incoming = items.ToDictionary(item => item.Id);
        foreach (var id in _cells.Keys.Where(id => !incoming.ContainsKey(id)).ToArray())
        {
            _cells[id].Button.Orphan();
            _cells.Remove(id);
        }
        _items.Clear();
        foreach (var (id, item) in incoming)
        {
            _items.Add(id, item);
            if (!_cells.TryGetValue(id, out var cell))
            {
                var button = new ContainerButton
                {
                    Name = id, MinWidth = 156, MinHeight = 96, HorizontalExpand = true, ToggleMode = true,
                    StyleClasses = { ContainerButton.StyleClassButton },
                    CanKeyboardFocus = true, KeyboardFocusOnClick = true,
                };
                var body = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 4 };
                var header = new BoxContainer { SeparationOverride = 4 };
                var icon = new TextureRect
                {
                    SetSize = new Vector2(32), CanShrink = true, Stretch = TextureRect.StretchMode.KeepAspectCentered,
                };
                var count = new Label();
                var fallback = new Label { Text = "?" };
                if (item.InputIcon != null)
                {
                    header.AddChild(new TextureRect
                    {
                        Texture = item.InputIcon, SetSize = new Vector2(32), CanShrink = true,
                        Stretch = TextureRect.StretchMode.KeepAspectCentered,
                    });
                    header.AddChild(new Label { Text = item.InputAmount.ToString() });
                    header.AddChild(new Label { Text = "→" });
                }
                var caption = new RichTextLabel { MaxWidth = 140 };
                var name = new RichTextLabel { Name = "ItemName", MaxWidth = 140 };
                header.AddChild(icon);
                header.AddChild(fallback);
                header.AddChild(new Label { Text = "×" });
                header.AddChild(count);
                body.AddChild(header);
                body.AddChild(name);
                body.AddChild(caption);
                button.AddChild(body);
                button.OnPressed += _ =>
                {
                    SelectedId = id;
                    UpdateSelection();
                    SelectionChanged?.Invoke();
                };
                Cells.AddChild(button);
                cell = (button, icon, count, fallback, caption, name);
                _cells.Add(id, cell);
            }
            cell.Icon.Texture = item.Icon;
            cell.Fallback.Visible = item.Icon == null;
            cell.Count.Text = item.Amount.ToString();
            cell.Name.SetMessage(FormattedMessage.FromUnformatted(item.Name));
            cell.Caption.Visible = !string.IsNullOrEmpty(item.Caption);
            cell.Caption.SetMessage(FormattedMessage.FromUnformatted(string.IsNullOrEmpty(item.Caption) ? "" :
                $"{item.Caption}\n{Loc.GetString(item.Available ? "frontline-ui-ready" : "frontline-ui-insufficient")}"));
            cell.Button.ToolTip = item.Tooltip;
            cell.Icon.Modulate = item.Available ? Color.White : Color.Gray;
        }
        if (SelectedId != null && !incoming.ContainsKey(SelectedId))
            SelectedId = null;
        var categories = new[] { "" }.Concat(incoming.Values.Select(item => item.Category).Distinct().Order()).ToArray();
        if (!_categories.SequenceEqual(categories))
        {
            _categories = categories;
            Categories.Clear();
            for (var i = 0; i < categories.Length; i++)
                Categories.AddItem(Loc.GetString(categories[i] == "" ? "frontline-ui-category-all" : categories[i]), i);
            if (!categories.Contains(SelectedCategory))
                SelectedCategory = "";
            Categories.SelectId(Array.IndexOf(categories, SelectedCategory));
        }
        ApplyFilter();
        UpdateSelection();
    }

    private void ApplyFilter()
    {
        foreach (var (id, cell) in _cells)
            cell.Button.Visible = SelectedCategory == "" || _items[id].Category == SelectedCategory;
        _empty.Visible = !_cells.Values.Any(cell => cell.Button.Visible);
    }

    private void UpdateSelection()
    {
        foreach (var (id, cell) in _cells)
            cell.Button.Pressed = id == SelectedId;
    }

    public static ScrollContainer CreateScroll(Control contents)
    {
        var scroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        scroll.AddChild(contents);
        return scroll;
    }

    public static BoxContainer Column(string heading)
    {
        var column = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, VerticalExpand = true, MinWidth = 156, Margin = new Thickness(6), SeparationOverride = 6 };
        column.AddChild(new Label { Text = heading, StyleClasses = { "LabelHeading" } });
        column.AddChild(new PanelContainer { StyleClasses = { "LowDivider" } });
        return column;
    }

    private sealed class ResponsiveGrid(int cellWidth) : GridContainer
    {
        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            var columns = float.IsFinite(availableSize.X) ? Math.Max(1, (int) (availableSize.X / cellWidth)) : 2;
            if (Columns != columns)
                Columns = columns;
            return base.MeasureOverride(availableSize);
        }
    }
}

public readonly record struct FrontlineJobView(
    string Id, string Name, Texture? Icon, TimeSpan Duration, TimeSpan Remaining, bool Processing);

/// <summary>Jobs have no server ID; keep positional rows while updating their real recipe and snapshot.</summary>
public sealed partial class FrontlineJobList : BoxContainer
{
    [Dependency] private IGameTiming _timing = default!;
    private readonly List<(BoxContainer Row, TextureRect Icon, RichTextLabel Text, ProgressBar Progress)> _rows = new();
    private readonly RichTextLabel _empty = new();
    private FrontlineJobView[] _jobs = [];
    private TimeSpan _received;

    public FrontlineJobList()
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        _empty.SetMessage(Loc.GetString("frontline-ui-queue-empty"));
        AddChild(_empty);
    }

    public void SetJobs(IEnumerable<FrontlineJobView> jobs)
    {
        _jobs = jobs.ToArray();
        _received = _timing.CurTime;
        while (_rows.Count > _jobs.Length)
        {
            _rows[^1].Row.Orphan();
            _rows.RemoveAt(_rows.Count - 1);
        }
        while (_rows.Count < _jobs.Length)
        {
            var row = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true };
            var header = new BoxContainer { HorizontalExpand = true };
            var icon = new TextureRect { SetSize = new Vector2(32), CanShrink = true, Stretch = TextureRect.StretchMode.KeepAspectCentered };
            var text = new RichTextLabel { HorizontalExpand = true };
            var progress = new ProgressBar { MinHeight = 12, MaxValue = 1 };
            header.AddChild(icon);
            header.AddChild(text);
            row.AddChild(header);
            row.AddChild(progress);
            AddChild(row);
            _rows.Add((row, icon, text, progress));
        }
        _empty.Visible = _jobs.Length == 0;
        Refresh();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Refresh();
    }

    private void Refresh()
    {
        for (var i = 0; i < _jobs.Length; i++)
        {
            var job = _jobs[i];
            var remaining = Math.Max(0, (job.Remaining - (job.Processing ? _timing.CurTime - _received : TimeSpan.Zero)).TotalSeconds);
            var row = _rows[i];
            row.Icon.Texture = job.Icon;
            row.Text.SetMessage(Loc.GetString("frontline-ui-job", ("position", i + 1), ("name", job.Name),
                ("status", Loc.GetString(job.Processing ? "frontline-ui-processing" : "frontline-ui-waiting")),
                ("seconds", Math.Ceiling(remaining))));
            row.Progress.Value = job.Duration > TimeSpan.Zero ? (float) Math.Clamp(1 - remaining / job.Duration.TotalSeconds, 0, 1) : 0;
            row.Progress.Visible = job.Processing;
        }
    }
}
