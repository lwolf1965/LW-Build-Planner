using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

public enum PlanRole { Consumer, Storage, Producer }

/// <summary>Block kinds (BlockDefinition.BlockKind.Guid) the planner cares about. Unknown kinds are never touched.</summary>
public static class PlanKinds
{
    // From LW Cargo Manager's discovery report (SE2 game assembly 4.0.0.x).
    public static readonly Guid CargoContainer = new("39699122-1fe4-4ed4-a9e2-9f2797067398");
    public static readonly Guid Connector = new("2a10a870-50cd-403c-ae16-810d35a9cad8");
    public static readonly Guid Crate = new("e2588836-5b6f-4513-9559-b36c9e488527");
    public static readonly Guid Locker = new("1102666b-35bf-4d55-8da4-25527b51eeaa");
    public static readonly Guid Refinery = new("7487a403-543f-445f-b9d6-d06d8ed27a47");
    public static readonly Guid Smelter = new("781de383-e433-417d-a0f7-7880072f3263");
    public static readonly Guid Assembler = new("a35d3e9c-a93b-4d95-b711-357ca1c66beb");
    public static readonly Guid Gearforge = new("dc3b0fbc-fc22-45fd-b950-2233a38eea47");
    public static readonly Guid Fabricator = new("99eb6da8-d60a-405e-b618-c665c868b3a5");

    /// <summary>Crafter machine name for a block kind (the names recipes use), or null when the block is not a crafter.</summary>
    public static string MachineOf(Guid? kind)
    {
        if (!kind.HasValue) return null;
        var k = kind.Value;
        if (k == Smelter) return "Smelter";
        if (k == Refinery) return "Refinery";
        if (k == Assembler) return "Assembler";
        if (k == Fabricator) return "Fabricator";
        if (k == Gearforge) return "Gearforge";
        return null;
    }

    public static PlanRole RoleOf(Guid? kind)
    {
        if (!kind.HasValue) return PlanRole.Consumer;
        var k = kind.Value;
        if (k == CargoContainer || k == Connector || k == Crate || k == Locker) return PlanRole.Storage;
        return MachineOf(kind) != null ? PlanRole.Producer : PlanRole.Consumer;
    }
}

public static class OrderPlan
{
    private static readonly Regex NoMakeRx = new(@"\[\s*No\s*Make\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MakeAmmoRx = new(@"\[\s*Make\s+Ammo\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// May the planner use this crafter? Yes by default (no tag needed). Not when it is named [NoMake], and never a
    /// block tagged [Make Ammo]: that one belongs to LW Cargo Manager's turret ammo.
    /// </summary>
    public static bool MayUse(string blockName)
        => string.IsNullOrEmpty(blockName) || !(NoMakeRx.IsMatch(blockName) || MakeAmmoRx.IsMatch(blockName));

    /// <summary>Recipe batches to queue for a missing amount: enough to cover it, capped by the batch limit. 0 = nothing to do.</summary>
    public static int Batches(long missing, double perBatch, int maxBatches)
    {
        if (missing <= 0 || perBatch <= 0 || maxBatches <= 0) return 0;
        return (int)Math.Min(maxBatches, Math.Ceiling(missing / perBatch));
    }

    /// <summary>
    /// Spreads crafts over idle crafters of one kind, one at a time round-robin, no crafter above the limit.
    /// The sum is less than total when every crafter is full: the rest is ordered on a later pass.
    /// </summary>
    public static int[] Split(int total, int crafters, int perCrafterLimit)
    {
        if (crafters <= 0) return Array.Empty<int>();
        var result = new int[crafters];
        if (total <= 0 || perCrafterLimit <= 0) return result;
        var left = total;
        for (var i = 0; left > 0 && result.Any(r => r < perCrafterLimit); i = (i + 1) % crafters)
        {
            if (result[i] >= perCrafterLimit) continue;
            result[i]++;
            left--;
        }
        return result;
    }
}
