using BattleTech;
using Harmony;
using ModTek.RuntimeLog;

namespace ModTek
{
    [HarmonyPatch(typeof(SimGameConstants))]
    [HarmonyPatch("FromJSON")]
    [HarmonyPatch(MethodType.Normal)]
    public static class SimGameConstants_FromJSON
    {
        public static bool Prepare() => ModTek.Enabled && ModTek.Config.EnableDebugLogging;

        public static void Postfix(SimGameConstants __instance, string json)
        {
            RLog.M.TWL(0, "SimGameConstants.FromJSON");
            RLog.M.WL(1, json);
        }
    }

}