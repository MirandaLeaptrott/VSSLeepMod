using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SimpleSleepSolution
{
    /// <summary>
    /// Patches BlockBed.OnBlockInteractStart to bypass the vanilla tiredness threshold check.
    /// Vanilla blocks bed use if ebt.Tiredness <= 8f. We set it to max before the check runs
    /// so players are never locked out of sleeping by the tiredness gate.
    /// Only affects players — BlockBed.OnBlockInteractStart is only called for player interactions.
    /// xskills reads Tiredness in its own DidUnmount postfix, which still functions normally.
    /// </summary>
    [HarmonyPatch(typeof(BlockBed), "OnBlockInteractStart")]
    public static class BlockBedInteractPatch
    {
        public static SimpleSleepSolutionMod Mod { get; set; }

        [HarmonyPrefix]
        public static void Prefix(IWorldAccessor world, IPlayer byPlayer)
        {
            if (world.Side != EnumAppSide.Server) return;
            if (byPlayer?.Entity == null) return;

            EntityBehaviorTiredness ebt = byPlayer.Entity.GetBehavior<EntityBehaviorTiredness>();
            if (ebt == null) return;

            // Set to max so the vanilla "not tired enough" gate never fires.
            // HoursPerDay / 2 is the cap used by EntityBehaviorTiredness.SlowTick.
            ebt.Tiredness = world.Calendar.HoursPerDay / 2f;
        }
    }
}
