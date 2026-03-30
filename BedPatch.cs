using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SimpleSleepSolution
{
    [HarmonyPatch(typeof(BlockEntityBed))]
    public static class BedPatch
    {
        // Set by SimpleSleepSolutionMod.StartServerSide before any beds can be interacted with
        public static SimpleSleepSolutionMod Mod { get; set; }

        [HarmonyPatch("DidMount")]
        [HarmonyPostfix]
        public static void DidMount_Postfix(BlockEntityBed __instance, EntityAgent entityAgent)
        {
            if (Mod == null) return;
            if (entityAgent == null) return;
            if (__instance.Api?.Side != Vintagestory.API.Common.EnumAppSide.Server) return;

            Mod.OnPlayerMountedBed(entityAgent);
        }

        [HarmonyPatch("DidUnmount")]
        [HarmonyPostfix]
        public static void DidUnmount_Postfix(BlockEntityBed __instance, EntityAgent entityAgent)
        {
            if (Mod == null) return;
            if (entityAgent == null) return;
            if (__instance.Api?.Side != Vintagestory.API.Common.EnumAppSide.Server) return;

            Mod.OnPlayerUnmountedBed(entityAgent);
        }
    }
}
