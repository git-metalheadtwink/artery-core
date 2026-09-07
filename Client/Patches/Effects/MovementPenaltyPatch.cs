using System.Reflection;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace ArteryCore.Patches.Effects
{
    /// <summary>
    /// Bruising slows the player down while it lasts.
    /// </summary>
    public sealed class BruiseMovementPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MovementContext),
                nameof(MovementContext.ClampSpeed));

        [PatchPostfix]
        private static void ApplyBruiseSpeedPenalty(Player ____player,
            ref float __result)
        {
            if (____player == null || __result <= 0f ||
                !ArteryConfig.EnableBruising.Value) return;

            ArteryController artery = ____player.GetComponent<ArteryController>();
            if (artery == null || artery.BruiseStrength <= 0f) return;

            __result *= Mathf.Lerp(1f,
                1f - ArteryConfig.BruiseSpeedPenalty.Value,
                artery.BruiseStrength);
        }
    }

    /// <summary>
    /// A spinal fracture is the heavy end of the fracture scale: no sprinting
    /// and a hard speed cap until painkillers are taken.
    /// </summary>
    public sealed class SpinalFractureMovementPatch : ModulePatch
    {
        private const float SpinalFractureSpeedLimit = 0.2f;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player),
                nameof(Player.UpdateSpeedLimitByHealth));

        [PatchPostfix]
        private static void ApplySpinalFractureSpeedLimit(Player __instance)
        {
            if (__instance == null || __instance.ActiveHealthController == null ||
                !ArteryConfig.EnableBoneFractures.Value ||
                !ArteryConfig.SpineFractureSpeedLimit.Value) return;

            ActiveHealthController health = __instance.ActiveHealthController;
            bool spinalFracture =
                health.FindExistingEffect<IFracture>(EBodyPart.Chest) != null ||
                health.FindExistingEffect<IFracture>(EBodyPart.Stomach) != null;
            if (!spinalFracture) return;
            if (health.FindExistingEffect<IPainKiller>() != null) return;

            __instance.MovementContext.EnableSprint(false);
            __instance.AddStateSpeedLimit(SpinalFractureSpeedLimit,
                Player.ESpeedLimit.HealthCondition);
        }
    }
}
