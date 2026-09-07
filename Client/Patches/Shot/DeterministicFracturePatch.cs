using System;
using System.Reflection;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace ArteryCore.Patches.Shot
{
    /// <summary>
    /// Vanilla rolls a random fracture chance while resolving a shot.
    /// ArteryCore decides fractures from bone geometry instead, so the random
    /// roll is suppressed for the duration of a shot ArteryCore is handling,
    /// and only the fracture ArteryCore itself asks for is let through.
    /// </summary>
    public sealed class DeterministicFracturePatch : ModulePatch
    {
        [ThreadStatic] internal static bool InsideShot;
        [ThreadStatic] internal static bool AllowNextFracture;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController),
                nameof(ActiveHealthController.DoFracture));

        [PatchPrefix]
        private static bool GateFracture()
        {
            if (AllowNextFracture)
            {
                AllowNextFracture = false;
                return true;
            }
            return !InsideShot;
        }
    }
}
