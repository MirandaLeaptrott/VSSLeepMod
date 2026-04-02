namespace SimpleSleepSolution
{
    public class SSSConfig
    {
        public int    TickMs          { get; set; } = 4000;
        public double WarnHour        { get; set; } = 15.0;
        public double EffectHour      { get; set; } = 16.0;
        public int    MaxStacks       { get; set; } = 6;
        public double MinSleepHours   { get; set; } = 4.0;
        public double MaxAwakeOnJoin  { get; set; } = 20.0;
        public float  WalkPerStack    { get; set; } = -0.08f;
        public float  MiningPerStack  { get; set; } = -0.10f;
        public float  MeleePerStack   { get; set; } = -0.08f;
        public float  HealingPerStack { get; set; } = -0.02f;
    }
}
