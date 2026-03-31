using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using ProtoBuf;

namespace SimpleSleepSolution
{
    public class SimpleSleepSolutionMod : ModSystem
    {
        // WatchedAttributes keys
        public const string ATTR_LAST_WOKE    = "sss.lastWokeAt";
        public const string ATTR_MOUNTED_AT   = "sss.mountedAt";
        public const string ATTR_WARNED       = "sss.warnedHour";
        public const string ATTR_STORM_WAKEUP = "sss.stormWakeup";
        public const string ATTR_STACKS       = "sss.stacks";

        // Stat modifier key — unique so we don't collide with other mods
        private const string STAT_KEY = "sss-tiredness";

        public const int CHAT_GROUP = 0;

        public ICoreServerAPI SAPI => sapi;
        private ICoreServerAPI sapi;
        private Harmony harmony;
        private double lastCheckedHour = -1;
        private SSSConfig config;

        private IServerNetworkChannel serverChannel;

        public override bool ShouldLoad(EnumAppSide side) => true;

        public override void StartPre(ICoreAPI api)
        {
            config = api.LoadModConfig<SSSConfig>("simplesleepsolution.json") ?? new SSSConfig();
            api.StoreModConfig(config, "simplesleepsolution.json");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            harmony = new Harmony("simplesleepsolution.patch");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            BedPatch.Mod              = this;
            WakeAllPlayersPatch.Mod   = this;
            BlockBedInteractPatch.Mod = this;

            serverChannel = api.Network
                .RegisterChannel("simplesleepsolution")
                .RegisterMessageType<SSSStackPacket>();

            api.Event.RegisterGameTickListener(OnSlowTick, config.TickMs);
            api.Event.PlayerNowPlaying += OnPlayerJoined;
            api.Event.PlayerDeath      += OnPlayerDeath;

            api.Logger.Notification("[SimpleSleepSolution] Loaded.");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            SSSHud hud = new SSSHud(api, config);

            api.Network
                .RegisterChannel("simplesleepsolution")
                .RegisterMessageType<SSSStackPacket>()
                .SetMessageHandler<SSSStackPacket>(packet => hud.OnStacksChanged(packet.Stacks, packet.MaxStacks));

            api.Event.RegisterGameTickListener(dt => hud.Update(), 200);
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll("simplesleepsolution.patch");
            harmony = null;
        }

        // ---------------------------------------------------------------------------
        // Player join
        // ---------------------------------------------------------------------------

        private void OnPlayerJoined(IServerPlayer splr)
        {
            if (splr?.Entity == null) return;

            double now        = sapi.World.Calendar.TotalHours;
            double lastWokeAt = splr.Entity.WatchedAttributes.GetDouble(ATTR_LAST_WOKE, -1.0);

            bool noRecord = lastWokeAt < 0;
            bool tooLong  = !noRecord && (now - lastWokeAt) > config.MaxAwakeOnJoin;

            if (noRecord || tooLong)
            {
                splr.Entity.WatchedAttributes.SetDouble(ATTR_LAST_WOKE, now);
                splr.Entity.WatchedAttributes.SetDouble(ATTR_WARNED, -1.0);
                splr.Entity.WatchedAttributes.SetInt(ATTR_STACKS, 0);
                RemoveTirednessEffects(splr.Entity);
            }
        }

        // ---------------------------------------------------------------------------
        // Player death
        // ---------------------------------------------------------------------------

        private void OnPlayerDeath(IServerPlayer splr, DamageSource damageSource)
        {
            if (splr?.Entity == null) return;
            splr.Entity.WatchedAttributes.SetDouble(ATTR_LAST_WOKE, sapi.World.Calendar.TotalHours);
            splr.Entity.WatchedAttributes.SetDouble(ATTR_WARNED, -1.0);
            splr.Entity.WatchedAttributes.SetInt(ATTR_STACKS, 0);
            RemoveTirednessEffects(splr.Entity);
        }

        // ---------------------------------------------------------------------------
        // Bed mount/unmount
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

            entityAgent.WatchedAttributes.SetDouble(ATTR_MOUNTED_AT, -1.0);

            bool stormWakeup = entityAgent.WatchedAttributes.GetInt(ATTR_STORM_WAKEUP, 0) > 0;
            entityAgent.WatchedAttributes.SetInt(ATTR_STORM_WAKEUP, 0);

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
                // Fully handled by WakeAllPlayersPatch
                return;
            }

            if (hoursInBed < config.MinSleepHours)
            {
                RemoveTirednessEffects(entityAgent);
                return;
            }

            // Valid full sleep
            entityAgent.WatchedAttributes.SetDouble(ATTR_LAST_WOKE, now);
            entityAgent.WatchedAttributes.SetDouble(ATTR_WARNED, -1.0);
            entityAgent.WatchedAttributes.SetInt(ATTR_STACKS, 0);
            RemoveTirednessEffects(entityAgent);

            IServerPlayer splr = GetServerPlayer(entityAgent);
            if (splr != null)
                serverChannel.SendPacket(new SSSStackPacket { Stacks = 0, MaxStacks = config.MaxStacks }, splr);
        }

        // ---------------------------------------------------------------------------
        // Storm wakeup
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

                EntityBehaviorTiredness tiredness = entity.GetBehavior<EntityBehaviorTiredness>();
                if (tiredness != null && tiredness.IsSleeping) continue;

                double lastWokeAt = entity.WatchedAttributes.GetDouble(ATTR_LAST_WOKE, -1.0);
                if (lastWokeAt < 0) continue;

                double hoursSinceWake = sapi.World.Calendar.TotalHours - lastWokeAt;

                if (hoursSinceWake < config.WarnHour)
                {
                    if (entity.WatchedAttributes.GetInt(ATTR_STACKS, 0) > 0)
                    {
                        RemoveTirednessEffects(entity);
                        SetStacks(splr, entity, 0);
                    }
                    continue;
                }

                if (hoursSinceWake < config.EffectHour)
                {
                    SendMessageOnce(splr, entity, 0, hoursSinceWake);
                    if (entity.WatchedAttributes.GetInt(ATTR_STACKS, 0) > 0)
                    {
                        RemoveTirednessEffects(entity);
                        SetStacks(splr, entity, 0);
                    }
                    continue;
                }

                int stacks = (int)Math.Min(Math.Floor(hoursSinceWake - config.EffectHour) + 1, config.MaxStacks);
                SendMessageOnce(splr, entity, stacks, hoursSinceWake);
                ApplyTirednessEffects(entity, stacks);
                SetStacks(splr, entity, stacks);
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
                5 => "simplesleepsolution:sss-stack5",
                _ => "simplesleepsolution:sss-stack6"
            };

            splr.SendMessage(CHAT_GROUP,
                Lang.GetL(splr.LanguageCode, msgKey, new object[] { (int)Math.Floor(hoursSinceWake) }),
                EnumChatType.Notification);

            TriggerTiredAnimations(splr, entity);
        }

        // ---------------------------------------------------------------------------
        // Animations
        // ---------------------------------------------------------------------------

        private void TriggerTiredAnimations(IServerPlayer splr, EntityAgent entity)
        {
            sapi.Network.SendEntityPacket(splr, entity.EntityId, 197,
                SerializerUtil.Serialize<string>("yawn"));

            long entityId = entity.EntityId;
            sapi.Event.RegisterCallback((float dt) =>
            {
                IServerPlayer p = sapi.World.PlayerByUid(splr.PlayerUID) as IServerPlayer;
                if (p?.Entity == null || p.ConnectionState != EnumClientState.Playing) return;
                sapi.Network.SendEntityPacket(p, entityId, 197,
                    SerializerUtil.Serialize<string>("stretch"));
            }, 3000);
        }

        // ---------------------------------------------------------------------------
        // Stat application — direct VS API, no xlib needed
        // ---------------------------------------------------------------------------

        private void ApplyTirednessEffects(EntityAgent entity, int stacks)
        {
            entity.Stats.Set("walkspeed",           STAT_KEY, config.WalkPerStack    * stacks, false);
            entity.Stats.Set("miningSpeedMul",      STAT_KEY, config.MiningPerStack  * stacks, false);
            entity.Stats.Set("meleeWeaponsDamage",  STAT_KEY, config.MeleePerStack   * stacks, false);
            entity.Stats.Set("healingeffectivness", STAT_KEY, config.HealingPerStack * stacks, false);
        }

        public void RemoveTirednessEffects(EntityAgent entity)
        {
            entity.Stats.Remove("walkspeed",           STAT_KEY);
            entity.Stats.Remove("miningSpeedMul",      STAT_KEY);
            entity.Stats.Remove("meleeWeaponsDamage",  STAT_KEY);
            entity.Stats.Remove("healingeffectivness", STAT_KEY);
        }

        // ---------------------------------------------------------------------------
        // Stack sync helpers
        // ---------------------------------------------------------------------------

        private void SetStacks(IServerPlayer splr, EntityAgent entity, int stacks)
        {
            int current = entity.WatchedAttributes.GetInt(ATTR_STACKS, 0);
            if (current == stacks) return;
            entity.WatchedAttributes.SetInt(ATTR_STACKS, stacks);
            serverChannel.SendPacket(new SSSStackPacket { Stacks = stacks, MaxStacks = config.MaxStacks }, splr);
        }

        private IServerPlayer GetServerPlayer(EntityAgent entityAgent)
        {
            string uid = (entityAgent as EntityPlayer)?.PlayerUID;
            if (uid == null) return null;
            return sapi.World.PlayerByUid(uid) as IServerPlayer;
        }
    }

    [ProtoContract]
    public class SSSStackPacket
    {
        [ProtoMember(1)] public int Stacks    { get; set; }
        [ProtoMember(2)] public int MaxStacks { get; set; }
    }
}
