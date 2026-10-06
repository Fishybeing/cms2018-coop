using HarmonyLib;
using UnityEngine;

namespace CMS2018Coop
{
    internal static class Sprint
    {
        public static bool Active;
    }

    [HarmonyPatch(typeof(FPSInputController), "Update")]
    internal static class SprintPatch
    {
        private static readonly AccessTools.FieldRef<FPSInputController, CharacterMotor> MotorRef = AccessTools.FieldRefAccess<FPSInputController, CharacterMotor>("motor");

        private static void Postfix(FPSInputController __instance)
        {
            Sprint.Active = false;
            Plugin p = Plugin.Instance;
            if (p == null || !p.SprintHeld) return;
            CharacterMotor motor = MotorRef(__instance);
            if (motor == null || !motor.canControl || motor.inputMoveDirection.sqrMagnitude < 0.01f) return;
            motor.inputMoveDirection *= p.SprintMultiplier;
            Sprint.Active = true;
        }
    }
}
