using System.Collections;
using HarmonyLib;

namespace CMS2018Coop
{
    internal static class Patches
    {
        public static void Apply()
        {
            Harmony harmony = new Harmony("fishybeing.cms2018coop");
            harmony.PatchAll(typeof(Patches).Assembly);
        }

        public static bool GuestConnected
        {
            get
            {
                Session s = Plugin.Instance != null ? Plugin.Instance.Session : null;
                return s != null && s.Connected && !s.IsHost && s.Remotes.Count > 0;
            }
        }

        public static IEnumerator Nothing()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(CarLifter), "Action")]
    internal static class LifterActionPatch
    {
        private static void Postfix(CarLifter __instance, int actionType, int __result)
        {
            if (CarSync.Applying || __result == 0) return;
            Session s = Plugin.Instance != null ? Plugin.Instance.Session : null;
            if (s == null || !s.Connected) return;
            s.Cars.OnLocalLift(__instance, actionType);
        }
    }

    [HarmonyPatch(typeof(OrderGenerator), "TakeJob")]
    internal static class TakeJobPatch
    {
        private static bool Prefix(ref IEnumerator __result)
        {
            if (!Patches.GuestConnected) return true;
            __result = Patches.Nothing();
            UIManager.Get().ShowInfoWindow("Only the host can take jobs in co-op");
            return false;
        }
    }

    [HarmonyPatch(typeof(OrderGenerator), "TakeMission")]
    internal static class TakeMissionPatch
    {
        private static bool Prefix(ref IEnumerator __result)
        {
            if (!Patches.GuestConnected) return true;
            __result = Patches.Nothing();
            return false;
        }
    }
}
