using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Content.Shared.War;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server.War;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarProductionPayment(
    [property: JsonRequired] string StackId,
    [property: JsonRequired] int Amount);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarProductionJobClaim(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string? Owner,
    [property: JsonRequired] bool Legacy,
    [property: JsonRequired] WarProductionPayment[] PaidInputs)
{
    public bool Equals(WarProductionJobClaim? other) => other != null && Id == other.Id &&
        Owner == other.Owner && Legacy == other.Legacy && PaidInputs.SequenceEqual(other.PaidInputs);
    public override int GetHashCode() => HashCode.Combine(Id, Owner, Legacy);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarProductionOutputClaim(
    [property: JsonRequired] string Owner,
    [property: JsonRequired] DateTimeOffset CompletedAtUtc);

/// <summary>Only shared entitlement/refund mechanics; native containers still own all physical goods.</summary>
public sealed partial class FrontlineProductionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _components = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private StackSystem _stacks = default!;

    public bool Flushing { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        EntityManager.BeforeEntityFlush += OnBeforeFlush;
        EntityManager.AfterEntityFlush += OnAfterFlush;
    }

    public override void Shutdown()
    {
        EntityManager.BeforeEntityFlush -= OnBeforeFlush;
        EntityManager.AfterEntityFlush -= OnAfterFlush;
        base.Shutdown();
    }

    private void OnBeforeFlush() => Flushing = true;
    private void OnAfterFlush() => Flushing = false;

    public bool CanAccess(EntityUid output, string? owner, DateTimeOffset? now = null)
    {
        if (!TryComp<FrontlineProductionClaimComponent>(output, out var claim))
            return owner == null;
        return claim.IsPersonal(now ?? DateTimeOffset.UtcNow) ? owner == claim.OwnerUserId : owner == null;
    }

    public void CompleteClaim(EntityUid output, string? owner, DateTimeOffset completedAt)
    {
        if (owner == null)
        {
            RemComp<FrontlineProductionClaimComponent>(output);
            return;
        }
        var claim = EnsureComp<FrontlineProductionClaimComponent>(output);
        claim.OwnerUserId = owner;
        claim.CompletedAtUtc = completedAt;
    }

    public WarProductionOutputClaim? CaptureClaim(EntityUid output)
    {
        if (!TryComp<FrontlineProductionClaimComponent>(output, out var claim))
            return null;
        var result = new WarProductionOutputClaim(claim.OwnerUserId, claim.CompletedAtUtc);
        ValidateClaim(result);
        return result;
    }

    public static void ValidateClaim(WarProductionOutputClaim? claim)
    {
        if (claim != null && (!ValidOwner(claim.Owner) || claim.CompletedAtUtc.Offset != TimeSpan.Zero ||
            claim.CompletedAtUtc == DateTimeOffset.MinValue))
            throw new InvalidDataException("Invalid production completion owner or UTC timestamp.");
    }

    public void RestoreClaim(EntityUid output, WarProductionOutputClaim? claim)
    {
        ValidateClaim(claim);
        if (claim != null)
            CompleteClaim(output, claim.Owner, claim.CompletedAtUtc);
    }

    public static bool ValidOwner(string owner) => Guid.TryParseExact(owner, "D", out var id) && id != Guid.Empty;

    public WarProductionJobClaim CaptureJob(Guid id, string? owner, bool legacy,
        Dictionary<ProtoId<StackPrototype>, int> inputs)
    {
        var claim = new WarProductionJobClaim(id, owner, legacy,
            inputs.OrderBy(entry => entry.Key.Id, StringComparer.Ordinal)
                .Select(entry => new WarProductionPayment(entry.Key.Id, entry.Value)).ToArray());
        ValidateJob(claim);
        return claim;
    }

    public void ValidateJob(WarProductionJobClaim claim)
    {
        if (claim == null || claim.Id == Guid.Empty || claim.PaidInputs == null ||
            (claim.Owner != null && !ValidOwner(claim.Owner)) ||
            (claim.Legacy ? claim.Owner != null || claim.PaidInputs.Length != 0 : claim.PaidInputs.Length == 0))
            throw new InvalidDataException("Invalid production ownership or original payment receipt.");
        var seen = new HashSet<string>();
        foreach (var payment in claim.PaidInputs)
        {
            if (payment == null || payment.Amount <= 0 || string.IsNullOrWhiteSpace(payment.StackId) ||
                !seen.Add(payment.StackId) ||
                !_prototypes.TryIndex(new ProtoId<StackPrototype>(payment.StackId), out var stack) || stack.Abstract ||
                !_prototypes.TryIndex(stack.Spawn, out var entity) || entity.Abstract ||
                !entity.TryGetComponent<StackComponent>(out var component, _components) || component.Unlimited ||
                component.StackTypeId.Id != payment.StackId)
                throw new InvalidDataException("Invalid original production material receipt.");
        }
    }

    // ponytail: bound cancellation staging at 4096 native stacks; do not accept an unrefundable new batch.
    public bool RefundFits(Dictionary<ProtoId<StackPrototype>, int> receipt, long batches)
    {
        if (batches <= 0)
            return false;
        long count = 0;
        foreach (var (type, amount) in receipt)
        {
            if (amount <= 0 || batches > long.MaxValue / amount ||
                !_prototypes.TryIndex(type, out var stack) ||
                !_prototypes.TryIndex(stack.Spawn, out var entity) ||
                !entity.TryGetComponent<StackComponent>(out var component, _components))
                return false;
            var maximum = _stacks.GetMaxCount(component);
            if (maximum <= 0)
                return false;
            var total = batches * amount;
            count += total / maximum + (total % maximum == 0 ? 0 : 1);
            if (count > 4096)
                return false;
        }
        return receipt.Count > 0;
    }

    public bool TryRefund(BaseContainer input, Dictionary<ProtoId<StackPrototype>, int> receipt, long batches,
        Func<bool> authorized, Func<bool> commit)
    {
        if (!RefundFits(receipt, batches) || !authorized())
            return false;
        var staged = new Dictionary<EntityUid, (ProtoId<StackPrototype> Type, int Count)>();
        var committed = false;
        try
        {
            foreach (var (type, amount) in receipt)
            {
                var remaining = amount * batches;
                while (remaining > 0)
                {
                    if (!authorized())
                        return false;
                    var uid = Spawn(_prototypes.Index(type).Spawn);
                    staged.Add(uid, (type, 0));
                    if (!Live(uid) || !TryComp<StackComponent>(uid, out var stack))
                        return false;
                    var count = (int) Math.Min(remaining, _stacks.GetMaxCount(stack));
                    staged[uid] = (type, count);
                    _stacks.SetCount((uid, stack), count);
                    if (!authorized() || !Live(uid) || !_containers.Insert(uid, input))
                        return false;
                    remaining -= count;
                }
            }
            if (!authorized() || staged.Any(entry => !Live(entry.Key) || !input.Contains(entry.Key) ||
                !TryComp<StackComponent>(entry.Key, out var stack) || stack.Unlimited ||
                stack.StackTypeId != entry.Value.Type || stack.Count != entry.Value.Count))
                return false;
            committed = commit();
            return committed;
        }
        finally
        {
            if (!committed)
            {
                Exception? failure = null;
                foreach (var uid in staged.Keys)
                {
                    if (TerminatingOrDeleted(uid)) continue;
                    try { QueueDel(uid); }
                    catch (Exception e) { failure ??= e; }
                }
                if (failure != null) throw failure;
                foreach (var uid in staged.Keys)
                    if (!TerminatingOrDeleted(uid)) Del(uid);
            }
        }
    }

    private bool Live(EntityUid uid) => !TerminatingOrDeleted(uid) && !EntityManager.IsQueuedForDeletion(uid);
}
