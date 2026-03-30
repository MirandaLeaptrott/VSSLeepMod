using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using XLib.XEffects;

namespace SimpleSleepSolution
{
    public class SimpleSleepSolutionMod : ModSystem
    {
        // Tuning values loaded from ModConfig/simplesleepsolution.json — see SSSConfig.cs for defaults.

        // WatchedAttributes keys — namespaced to avoid collisions
        public const string ATTR_LAST_WOKE    = "sss.lastWokeAt";
        public const string ATTR_MOUNTED_AT   = "sss.mountedAt";
        public const string ATTR_WARNED       = "sss.warnedHour";
        // Flag written by WakeAllPlayers prefix before TryUnmount fires,
        // read and immediately cleared by DidUnmount postfix.
        public const string ATTR_STORM_WAKEUP = "sss.stormWakeup";

        // XEffects stat effect names — must be unique across all mods
        public const string FX_WALK    = "sss-tired-walkspeed";
        public const string FX_MINING  = "sss-tired-mining";
        public const string FX_MELEE   = "sss-tired-melee";
        public const string FX_HEALING = "sss-tired-healing";

        // Chat group 0 = general channel, confirmed by xskills for player notification messages
        public const int CHAT_GROUP = 0;

        public ICoreServerAPI SAPI => sapi;
        private ICoreServerAPI sapi;
        private Harmony harmony;
        private double lastCheckedHour = -1;
        private SSSConfig config;

        // Load on both sides: server does all logic, client needs effect types
        // registered so the XEffects HUD can display them.
        public override bool ShouldLoad(EnumAppSide side) => true;

        public override void StartPre(ICoreAPI api)
        {
            // Load config — creates with defaults on first run
            config = api.LoadModConfig<SSSConfig>("simplesleepsolution.json") ?? new SSSConfig();
            api.StoreModConfig(config, "simplesleepsolution.json");

            // Register effect types on both sides so XEffects HUD works client-side.
            // AffectedEntityBehavior.CreateEffectsFromTree silently skips unknown types,
            // so without this the HUD would show nothing even though effects are active.
            XEffectsSystem xfx = api.ModLoader.GetModSystem<XEffectsSystem>();
            if (xfx == null)
            {
                api.Logger.Error("[SimpleSleepSolution] XEffectsSystem not found. Is xlib loaded?");
                return;
            }

            xfx.RegisterEffectType(new EffectType(FX_WALK,    typeof(StatEffect), null, null, "simplesleepsolution"));
            xfx.RegisterEffectType(new EffectType(FX_MINING,  typeof(StatEffect), null, null, "simplesleepsolution"));
            xfx.RegisterEffectType(new EffectType(FX_MELEE,   typeof(StatEffect), null, null, "simplesleepsolution"));
            xfx.RegisterEffectType(new EffectType(FX_HEALING, typeof(StatEffect), null, null, "simplesleepsolution"));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            harmony = new Harmony("simplesleepsolution.patch");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            BedPatch.Mod            = this;
            WakeAllPlayersPatch.Mod = this;
            BlockBedInteractPatch.Mod = this;

            api.Event.RegisterGameTickListener(OnSlowTick, config.TickMs);
            api.Event.PlayerNowPlaying += OnPlayerJoined;
            api.Event.PlayerDeath += OnPlayerDeath;

            api.Logger.Notification("[SimpleSleepSolution] Loaded.");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            // Nothing to do client-side beyond effect type registration in StartPre.
            // Stats are applied server-side; WatchedAttributes sync handles the rest.
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll("simplesleepsolution.patch");
            harmony = null;
        }

        // ---------------------------------------------------------------------------
        // Player join — set default lastWokeAt if not present
        // ---------------------------------------------------------------------------

        private void OnPlayerJoined(IServerPlayer splr)
        {
            if (splr?.Entity == null) return;

            double now = sapi.World.Calendar.TotalHours;
            double lastWokeAt = splr.Entity.WatchedAttributes.GetDouble(ATTR_LAST_WOKE, -1.0);

            // Reset to now if:
            // - No record exists (first join, or mod newly added)
            // - They've been "awake" for over 20 hours — likely offline time, don't punish
            bool noRecord = lastWokeAt < 0;
            bool tooLong  = !noRecord && (now - lastWokeAt) > config.MaxAwakeOnJoin;

            if (noRecord || tooLong)
            {
                splr.Entity.WatchedAttributes.SetDouble(ATTR_LAST_WOKE, now);
                splr.Entity.WatchedAttributes.SetDouble(ATTR_WARNED, -1.0);
                RemoveTirednessEffects(splr.Entity);
            }
            // Otherwise leave the clock running — they're rejoining within a normal window
        }


        // ---------------------------------------------------------------------------
        // Player death — reset wake time so respawn feels fresh
        // ---------------------------------------------------------------------------

        private void OnPlayerDeath(IServerPlayer splr, DamageSource damageSource)
        {
            if (splr?.Entity == null) return;

            // Set lastWokeAt to now so the respawned player starts their tiredness
            // clock from zero. XEffects clears our stat effects automatically on death
            // via ExpiresAtDeath, so we just need to reset the attributes.
            splr.Entity.WatchedAttributes.SetDouble(ATTR_LAST_WOKE, sapi.World.Calendar.TotalHours);
            splr.Entity.WatchedAttributes.SetDouble(ATTR_WARNED, -1.0);
        }

        // ---------------------------------------------------------------------------
        // Bed mount/unmount — called by Harmony patches on BlockEntityBed
        // ---------------------------------------------------------------------------

        public void OnPlayerMountedBed(EntityAgent entityAgent)
        {
            if (entityAgent?.World?.Side != EnumAppSide.Server) return;
            entityAgent.WatchedAttributes.SetDouble(ATTR_MOUNTED_AT, entityAgent.World.Calendar.TotalHours);
        }

        public void OnPlayerUnmountedBed(EntityAgent entityAgent)
        {
            if (entityAgent?.World?.Side != EnumAppSide.Server) return;

            double mountedAt = entityAgent.WatchedAttributes.GetDouble(ATTR_MOUNTED_AT, -1.0);
            if (mountedAt < 0) return;

            double now        = entityAgent.World.Calendar.TotalHours;
            double hoursInBed = now - mountedAt;

            // Always clear mount timestamp
            entityAgent.WatchedAttributes.SetDouble(ATTR_MOUNTED_AT, -1.0);

            // Check and immediately clear the storm wakeup flag.
            // This may not be set if a third-party mod (e.g. Temporal Symphony) handles
            // storm wakeups through a different path than ModSleeping.WakeAllPlayers.
            bool stormWakeup = entityAgent.WatchedAttributes.GetInt(ATTR_STORM_WAKEUP, 0) > 0;
            entityAgent.WatchedAttributes.SetInt(ATTR_STORM_WAKEUP, 0);

            // Also detect storm via direct API check as a fallback for mods that bypass
            // WakeAllPlayers — if a storm is active and they're being unmounted, it's a storm.
            if (!stormWakeup)
            {
                try
                {
                    SystemTemporalStability temporal = entityAgent.Api.ModLoader.GetModSystem<SystemTemporalStability>(true);
                    stormWakeup = temporal != null && (temporal.StormStrength > 0f || temporal.StormData?.nowStormActive == true);
                }
                catch { }
            }

            if (stormWakeup)
            {
                // Already handled by WakeAllPlayersPatch — reset and message were sent there.
                // Just return to avoid double-processing.
                return;
            }

            if (hoursInBed < config.MinSleepHours)
            {
                // Not long enough to count — but still remove effects unconditionally.
                // Covers edge cases where effects were somehow applied before bed (e.g.
                // player was tired, got into bed briefly, got kicked for any reason).
                RemoveTirednessEffects(entityAgent);
                return;
            }

            // Valid full sleep
            entityAgent.WatchedAttributes.SetDouble(ATTR_LAST_WOKE, now);
            entityAgent.WatchedAttributes.SetDouble(ATTR_WARNED, -1.0);
            RemoveTirednessEffects(entityAgent);
        }

        // ---------------------------------------------------------------------------
        // Called by WakeAllPlayersPatch prefix — fires before TryUnmount on each player
        // ---------------------------------------------------------------------------

        public void OnStormWakingPlayer(EntityAgent entityAgent)
        {
            if (entityAgent?.World?.Side != EnumAppSide.Server) return;
            entityAgent.WatchedAttributes.SetInt(ATTR_STORM_WAKEUP, 1);
        }

        // ---------------------------------------------------------------------------
        // Hourly tick
        // ---------------------------------------------------------------------------

        private void OnSlowTick(float dt)
        {
            double currentHour = Math.Floor(sapi.World.Calendar.TotalHours);
            if (currentHour <= lastCheckedHour) return;
            lastCheckedHour = currentHour;

            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                IServerPlayer splr = player as IServerPlayer;
                if (splr == null) continue;
                if (splr.ConnectionState != EnumClientState.Playing) continue;
                if (splr.WorldData?.CurrentGameMode == EnumGameMode.Spectator) continue;

                EntityAgent entity = splr.Entity;
                if (entity == null || !entity.Alive) continue;

                // Skip while in bed
                EntityBehaviorTiredness tiredness = entity.GetBehavior<EntityBehaviorTiredness>();
                if (tiredness != null && tiredness.IsSleeping) continue;

                double lastWokeAt = entity.WatchedAttributes.GetDouble(ATTR_LAST_WOKE, -1.0);
                if (lastWokeAt < 0) continue; // still unset somehow, skip safely

                double hoursSinceWake = sapi.World.Calendar.TotalHours - lastWokeAt;

                if (hoursSinceWake < config.WarnHour)
                {
                    // Fine — only clean up effects if they're actually present.
                    // Avoids iterating the effects dict and syncing WatchedAttributes every hour
                    // for players who are well-rested and have no effects active.
                    AffectedEntityBehavior affected = entity.GetBehavior<AffectedEntityBehavior>();
                    if (affected != null &&
                        (affected.IsAffectedBy(FX_WALK) || affected.IsAffectedBy(FX_MINING) ||
                         affected.IsAffectedBy(FX_MELEE) || affected.IsAffectedBy(FX_HEALING)))
                    {
                        RemoveTirednessEffects(entity);
                    }
                    continue;
                }

                if (hoursSinceWake < config.EffectHour)
                {
                    // Warning zone — send message, ensure no effects active
                    SendMessageOnce(splr, entity, 0, hoursSinceWake);
                    AffectedEntityBehavior affected = entity.GetBehavior<AffectedEntityBehavior>();
                    if (affected != null &&
                        (affected.IsAffectedBy(FX_WALK) || affected.IsAffectedBy(FX_MINING) ||
                         affected.IsAffectedBy(FX_MELEE) || affected.IsAffectedBy(FX_HEALING)))
                    {
                        RemoveTirednessEffects(entity);
                    }
                    continue;
                }

                // Effect zone: stack 1 at hour 16, stack 2 at 17 ... capped at MaxStacks
                int stacks = (int)Math.Min(Math.Floor(hoursSinceWake - config.EffectHour) + 1, config.MaxStacks);
                SendMessageOnce(splr, entity, stacks, hoursSinceWake);
                ApplyTirednessEffects(entity, stacks);
            }
        }

        // ---------------------------------------------------------------------------
        // Messaging
        // ---------------------------------------------------------------------------

        private void SendMessageOnce(IServerPlayer splr, EntityAgent entity, int stacks, double hoursSinceWake)
        {
            double lastWarnedHour = entity.WatchedAttributes.GetDouble(ATTR_WARNED, -1.0);
            double currentHour    = Math.Floor(sapi.World.Calendar.TotalHours);
            if (Math.Abs(lastWarnedHour - currentHour) < 0.5) return;

            entity.WatchedAttributes.SetDouble(ATTR_WARNED, currentHour);

            string msgKey = stacks switch
            {
                0 => "simplesleepsolution:sss-warning",
                1 => "simplesleepsolution:sss-stack1",
                2 => "simplesleepsolution:sss-stack2",
                3 => "simplesleepsolution:sss-stack3",
                4 => "simplesleepsolution:sss-stack4",
                _ => "simplesleepsolution:sss-stack5"
            };

            splr.SendMessage(CHAT_GROUP,
                Lang.GetL(splr.LanguageCode, msgKey, new object[] { (int)Math.Floor(hoursSinceWake) }),
                EnumChatType.Notification);

            // Play yawn then stretch with a delay between them
            TriggerTiredAnimations(splr, entity);
        }

        // ---------------------------------------------------------------------------
        // Animations
        // ---------------------------------------------------------------------------

        private void TriggerTiredAnimations(IServerPlayer splr, EntityAgent entity)
        {
            // Send yawn immediately via packet 197 (server -> client animation trigger,
            // confirmed from EntityPlayer.OnReceivedServerPacket)
            sapi.Network.SendEntityPacket(splr, entity.EntityId, 197,
                SerializerUtil.Serialize<string>("yawn"));

            // Queue stretch after yawn has had time to play (~3 real seconds)
            long entityId = entity.EntityId;
            sapi.Event.RegisterCallback((float dt) =>
            {
                // Re-fetch player in case they disconnected during the delay
                IServerPlayer p = sapi.World.PlayerByUid(splr.PlayerUID) as IServerPlayer;
                if (p?.Entity == null || p.ConnectionState != EnumClientState.Playing) return;
                sapi.Network.SendEntityPacket(p, entityId, 197,
                    SerializerUtil.Serialize<string>("stretch"));
            }, 3000);
        }

                // ---------------------------------------------------------------------------
        // XEffects application
        // ---------------------------------------------------------------------------

        private void ApplyTirednessEffects(EntityAgent entity, int stacks)
        {
            XEffectsSystem xfx = sapi.ModLoader.GetModSystem<XEffectsSystem>();
            if (xfx == null) return;

            AffectedEntityBehavior affected = entity.GetBehavior<AffectedEntityBehavior>();
            if (affected == null) return;

            ApplyOrUpdateStatEffect(xfx, affected, FX_WALK,    "walkspeed",           config.WalkPerStack,    stacks);
            ApplyOrUpdateStatEffect(xfx, affected, FX_MINING,  "miningSpeedMul",      config.MiningPerStack,  stacks);
            ApplyOrUpdateStatEffect(xfx, affected, FX_MELEE,   "meleeWeaponsDamage",  config.MeleePerStack,   stacks);
            ApplyOrUpdateStatEffect(xfx, affected, FX_HEALING, "healingeffectivness", config.HealingPerStack, stacks);
        }

        private void ApplyOrUpdateStatEffect(XEffectsSystem xfx, AffectedEntityBehavior affected,
            string effectName, string statName, float intensityPerStack, int stacks)
        {
            Effect existing = affected.Effect(effectName);

            if (existing is StatEffect existingStat)
            {
                // Already running — only call Update if stacks changed to avoid unnecessary Stats.Set
                if (existingStat.Stacks != stacks)
                    existingStat.Update(intensityPerStack, stacks);
                return;
            }

            EffectType type = xfx.EffectType(effectName);
            if (type == null) return;

            // duration=0 → no time expiry.
            // Intensity is negative so ExpiresThroughIntensity won't fire on zero-check.
            // ExpiresAtDeath is fine. We own removal via RemoveTirednessEffects.
            StatEffect effect = new StatEffect(type, 0f, statName, config.MaxStacks, stacks, intensityPerStack);
            affected.AddEffect(effect);
            affected.MarkDirty();
        }

        public void RemoveTirednessEffects(EntityAgent entity)
        {
            AffectedEntityBehavior affected = entity.GetBehavior<AffectedEntityBehavior>();
            if (affected == null) return;

            bool anyRemoved  = affected.RemoveEffect(FX_WALK);
            anyRemoved      |= affected.RemoveEffect(FX_MINING);
            anyRemoved      |= affected.RemoveEffect(FX_MELEE);
            anyRemoved      |= affected.RemoveEffect(FX_HEALING);

            if (anyRemoved) affected.MarkDirty();
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private IServerPlayer GetServerPlayer(EntityAgent entityAgent)
        {
            string uid = (entityAgent as EntityPlayer)?.PlayerUID;
            if (uid == null) return null;
            return sapi.World.PlayerByUid(uid) as IServerPlayer;
        }
    }
}
