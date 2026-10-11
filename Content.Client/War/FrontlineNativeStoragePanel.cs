using System.Linq;
using Content.Client.Storage;
using Content.Client.UserInterface.Systems.Storage.Controls;
using Content.Shared.Storage;
using Content.Shared.War;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Client.War;

/// <summary>Mounts the existing native storage window, not a second inventory implementation.</summary>
public sealed partial class FrontlineNativeStoragePanel : BoxContainer
{
    [Dependency] private IEntityManager _entities = default!;
    public EntityUid? Machine { get; set; }
    private readonly bool _output;

    public FrontlineNativeStoragePanel(EntityUid? machine, bool output)
    {
        IoCManager.InjectDependencies(this);
        Machine = machine;
        _output = output;
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;
        MinHeight = 64;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (Machine is not { } machine) return;
        if (_entities.HasComponent<FrontlineStockpileComponent>(machine))
        {
            TryAttach(machine);
            return;
        }
        var query = _entities.EntityQueryEnumerator<FrontlineProductionStorageComponent>();
        while (query.MoveNext(out var uid, out var link))
        {
            if (link.Machine == machine && link.Output == _output && TryAttach(uid)) return;
        }
    }

    private bool TryAttach(EntityUid uid)
    {
        if (!_entities.System<SharedUserInterfaceSystem>().TryGetOpenUi<StorageBoundUserInterface>(uid,
            StorageComponent.StorageUiKey.Key, out var bui) || bui.Window is not { } window) return false;
        if (window.Parent != this)
        {
            window.Orphan();
            AddChild(window);
        }
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var window in Children.OfType<StorageWindow>().ToArray()) window.Orphan();
        base.Dispose(disposing);
    }
}
