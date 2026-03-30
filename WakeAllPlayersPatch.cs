using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SimpleSleepSolution
{
    /// <summary>
    /// Patches ModSleeping.WakeAllPlayers which fires when a temporal storm hits.
    /// We reset ALL online players — sleeping or awake — wiping stacks and resetting
    /// their wake clock to now. The storm is a shared event that affects everyone.
    /// </summary>
    [HarmonyPatch(typeof(ModSleeping), "WakeAllPlayers")]
    public static class WakeAllPlayersPatch
    {
        public static SimpleSleepSolutionMod Mod { get; set; }

        [HarmonyPrefix]
        public static void Prefix(ModSleeping __instance)
        {
            if (Mod == null) return;

            ICoreServerAPI sapi = Mod.SAPI;
            if (sapi == null) return;

            double now = sapi.World.Calendar.TotalHours;

            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                IServerPlayer splr = player as IServerPlayer;
                if (splr?.ConnectionState != EnumClientState.Playing) continue;
                if (splr.WorldData?.CurrentGameMode == EnumGameMode.Spectator) continue;
                if (splr.Entity == null) continue;

                // Reset wake clock and wipe all tiredness effects for everyone
                splr.Entity.WatchedAttributes.SetDouble(SimpleSleepSolutionMod.ATTR_LAST_WOKE, now);
                splr.Entity.WatchedAttributes.SetDouble(SimpleSleepSolutionMod.ATTR_WARNED, -1.0);
                Mod.RemoveTirednessEffects(splr.Entity);

                splr.SendMessage(SimpleSleepSolutionMod.CHAT_GROUP,
                    Lang.GetL(splr.LanguageCode, "simplesleepsolution:sss-storm-wakeup"),
                    EnumChatType.Notification);
            }

            // For sleeping players, also flag them so DidUnmount knows not to re-process
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                IServerPlayer splr = player as IServerPlayer;
                if (splr?.Entity == null) continue;
                EntityBehaviorTiredness tiredness = splr.Entity.GetBehavior<EntityBehaviorTiredness>();
                if (tiredness != null && tiredness.IsSleeping)
                    splr.Entity.WatchedAttributes.SetInt(SimpleSleepSolutionMod.ATTR_STORM_WAKEUP, 1);
            }
        }
    }
}
