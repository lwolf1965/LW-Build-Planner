using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>
/// The planner's running orders.
///  - Waiting: parts not queued yet, because no crafter of the right kind was free.
///  - Queued: parts queued in a crafter and not finished yet.
/// An order is plain: "order all" makes every part of the build list, whatever is in stock; "order missing" makes
/// only what storage and the running orders do not cover. Nothing is reserved behind the
/// player's back: what the window shows is what is counted.
/// The ledger also keeps the history of the running batch (what was ordered per part, in the order it was ordered,
/// and how much of it is finished) so the window can show progress. The history ends when the next order starts
/// after everything finished, or when the orders are cleared.
/// </summary>
public sealed class OrderLedger
{
    private readonly Dictionary<string, long> waiting = new();
    private readonly Dictionary<(int Crafter, string Item), long> queued = new();
    private readonly Dictionary<string, long> ordered = new();
    private readonly Dictionary<string, long> doneCount = new();
    private readonly List<string> sequence = new();
    private readonly Dictionary<string, long> reserved = new();

    /// <summary>
    /// Parts kept for blocks that already left the build list (covered from storage or all their parts handed out,
    /// M3): they are in storage or on their way, waiting to be welded. They are not offered to the list again, so a
    /// second "Order missing" does not count them twice. Kept when orders stop or finish; shrinks when the parts leave
    /// storage (welded: ClampReserved); gone with the world.
    /// </summary>
    public IReadOnlyDictionary<string, long> Reserved => reserved;

    public void Reserve(IReadOnlyDictionary<string, long> parts)
    {
        foreach (var part in parts)
            if (part.Value > 0) reserved[part.Key] = (reserved.TryGetValue(part.Key, out var r) ? r : 0) + part.Value;
    }

    /// <summary>Kept parts that are no longer in storage nor on their way were used (welded): they are not kept any more.</summary>
    public void ClampReserved(IReadOnlyDictionary<string, long> stock)
    {
        foreach (var item in reserved.Keys.ToList())
        {
            var there = (stock.TryGetValue(item, out var s) ? s : 0) + Incoming(item);
            if (there <= 0) reserved.Remove(item);
            else if (reserved[item] > there) reserved[item] = there;
        }
    }

    /// <summary>Parts the build list can count on: storage + on their way - kept for blocks already off the list.</summary>
    public long Available(string item, long stock)
        => Math.Max(0, stock + Incoming(item) - (reserved.TryGetValue(item, out var r) ? r : 0));

    /// <summary>A new world: nothing is kept.</summary>
    public void ClearReserved() => reserved.Clear();

    public IReadOnlyDictionary<string, long> Waiting => waiting;

    /// <summary>Parts of the running (or last finished) batch, oldest order first.</summary>
    public IReadOnlyList<string> Sequence => sequence;

    /// <summary>Everything ordered of this part in the batch.</summary>
    public long Ordered(string item) => ordered.TryGetValue(item, out var o) ? o : 0;

    /// <summary>Parts of this item that arrived from the queued lines, never more than was ordered.</summary>
    public long Done(string item) => Math.Min(Ordered(item), doneCount.TryGetValue(item, out var d) ? d : 0);

    /// <summary>The waiting parts, oldest order first: the crafters get them in this order.</summary>
    public IEnumerable<string> WaitingOrder() => sequence.Where(waiting.ContainsKey);

    public long QueuedOf(string item) => queued.Where(q => q.Key.Item == item).Sum(q => q.Value);

    /// <summary>Parts still counted as queued in this crafter, whatever the item.</summary>
    public long QueuedOfCrafter(int crafter) => queued.Where(q => q.Key.Crafter == crafter).Sum(q => q.Value);

    /// <summary>Not in stock yet but on its way: waiting + queued.</summary>
    public long Incoming(string item) => (waiting.TryGetValue(item, out var w) ? w : 0) + QueuedOf(item);

    public Dictionary<string, long> IncomingAll()
    {
        var all = new Dictionary<string, long>();
        foreach (var item in waiting.Keys.Concat(queued.Keys.Select(k => k.Item)).Distinct())
        {
            var amount = Incoming(item);
            if (amount > 0) all[item] = amount;
        }
        return all;
    }

    public IEnumerable<(int Crafter, string Item, long Amount)> QueuedLines => queued.Select(q => (q.Key.Crafter, q.Key.Item, q.Value));

    public IEnumerable<int> CraftersWithOrders => queued.Keys.Select(k => k.Crafter).Distinct();

    /// <summary>True while something is waiting or queued (the planner has to keep watching).</summary>
    public bool Active => waiting.Values.Any(v => v > 0) || queued.Count > 0;

    /// <summary>What the stock and the running orders do not cover (parts kept for blocks off the list do not count).</summary>
    public long Missing(string item, long wanted, long stock) => Math.Max(0, wanted - Available(item, stock));

    /// <summary>
    /// Parts an order has to make. onlyMissing = false: everything asked for ("order all").
    /// onlyMissing = true: only what stock and running orders do not cover.
    /// </summary>
    public Dictionary<string, long> NeedForNew(IReadOnlyDictionary<string, long> wanted, IReadOnlyDictionary<string, long> stock, bool onlyMissing)
    {
        var need = new Dictionary<string, long>();
        foreach (var want in wanted)
        {
            if (want.Value <= 0) continue;
            var amount = onlyMissing ? Missing(want.Key, want.Value, stock.TryGetValue(want.Key, out var have) ? have : 0) : want.Value;
            if (amount > 0) need[want.Key] = amount;
        }
        return need;
    }

    /// <summary>Records an order: its parts wait for a crafter.</summary>
    public void Commit(IReadOnlyDictionary<string, long> need)
    {
        if (!Active) ClearHistory(); // the last batch is finished: a new one starts
        foreach (var n in need)
        {
            if (n.Value <= 0) continue;
            waiting[n.Key] = (waiting.TryGetValue(n.Key, out var w) ? w : 0) + n.Value;
            ordered[n.Key] = Ordered(n.Key) + n.Value;
            if (!sequence.Contains(n.Key)) sequence.Add(n.Key);
        }
    }

    private void ClearHistory()
    {
        ordered.Clear();
        doneCount.Clear();
        sequence.Clear();
    }

    /// <summary>Parts were queued in a crafter: they leave Waiting (a craft may make more than was waiting).</summary>
    public void MarkQueued(int crafter, string item, long amount)
    {
        if (amount <= 0) return;
        var left = (waiting.TryGetValue(item, out var w) ? w : 0) - amount;
        if (left > 0) waiting[item] = left;
        else waiting.Remove(item);
        queued[(crafter, item)] = (queued.TryGetValue((crafter, item), out var q) ? q : 0) + amount;
    }

    /// <summary>
    /// Parts of this item arrived in stock: they are taken off the queued lines. The crafters in idleFirst (not crafting
    /// now, so most likely the ones that finished) are credited first, then the lowest crafter id.
    /// Returns what was left over (more arrived than was on order).
    /// </summary>
    public long Arrived(string item, long amount, IReadOnlyCollection<int> idleFirst = null)
    {
        foreach (var key in queued.Keys.Where(k => k.Item == item)
                     .OrderBy(k => idleFirst != null && idleFirst.Contains(k.Crafter) ? 0 : 1).ThenBy(k => k.Crafter).ToList())
        {
            if (amount <= 0) break;
            var take = Math.Min(queued[key], amount);
            amount -= take;
            doneCount[item] = (doneCount.TryGetValue(item, out var d) ? d : 0) + take;
            if (queued[key] - take > 0) queued[key] -= take;
            else queued.Remove(key);
        }
        return Math.Max(0, amount);
    }

    /// <summary>
    /// The crafter no longer has this part's recipe in its queue (the game's own check): it has made all of its line,
    /// even when the parts left storage at once (taken for welding) and were never seen arriving. The rest of the
    /// line counts as done. Returns how many were credited this way.
    /// </summary>
    public long CrafterFinished(int crafter, string item)
    {
        if (!queued.TryGetValue((crafter, item), out var left) || left <= 0) return 0;
        queued.Remove((crafter, item));
        doneCount[item] = (doneCount.TryGetValue(item, out var d) ? d : 0) + left;
        return left;
    }

    /// <summary>The crafter is gone (removed, grid lost): its lines are closed. Returns the items it had.</summary>
    public List<string> CrafterDone(int crafter)
    {
        var keys = queued.Keys.Where(k => k.Crafter == crafter).ToList();
        foreach (var key in keys) queued.Remove(key);
        return keys.Select(k => k.Item).ToList();
    }

    /// <summary>Gives up on an item that is still waiting (nobody can make it).</summary>
    public void DropWaiting(string item) => waiting.Remove(item);

    /// <summary>Stops queueing new parts; what is already in the crafters keeps running.</summary>
    public void StopWaiting() => waiting.Clear();

    /// <summary>Forgets all orders (the crafters keep what is in their queues).</summary>
    public void Clear()
    {
        waiting.Clear();
        queued.Clear();
        ClearHistory();
    }
}

/// <summary>How many blocks one key press adds and how many a list line may hold.</summary>
public static class PlanLimits
{
    /// <summary>
    /// Armor is told by "Armor" in the block definition's type name or in its text ("guid : Vanilla\Armors\Cube\...
    /// \ArmorCubeLight250_LightArmorCubeBlockDefinition.def"), never by the shown name, which is in the game's language.
    /// (The type name alone missed vanilla armor: Light Armor Cube got the 50 limit, 2026-11-19.)
    /// </summary>
    public static bool IsArmor(string definitionTypeName, string definitionText)
        => (definitionTypeName?.Contains("Armor", System.StringComparison.OrdinalIgnoreCase) ?? false)
           || (definitionText?.Contains("Armor", System.StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Armor is placed by the hundred; working blocks (thrusters, tanks ...) by the handful. The sizes come from the
    /// settings (defaults: armor 1 / Shift 10 / Ctrl 100 / Alt 500, other blocks 1 / 5 / 10 / 25). Alt beats Ctrl beats Shift.
    /// </summary>
    public static int Step(bool armor, bool shift, bool ctrl, bool alt = false)
    {
        var s = PlannerSettings.Current;
        return armor ? (alt ? s.ArmorStepAlt : ctrl ? s.ArmorStepCtrl : shift ? s.ArmorStepShift : s.ArmorStep)
                     : (alt ? s.StepAlt : ctrl ? s.StepCtrl : shift ? s.StepShift : s.Step);
    }

    /// <summary>Most blocks per list line (settings; defaults armor 1000, other blocks 50).</summary>
    public static int Max(bool armor) => armor ? PlannerSettings.Current.ArmorMaxPerLine : PlannerSettings.Current.MaxPerLine;

    /// <summary>The count after adding (or removing) delta, kept between 0 and the limit.</summary>
    public static int Apply(int current, int delta, bool armor) => Math.Max(0, Math.Min(Max(armor), current + delta));
}
