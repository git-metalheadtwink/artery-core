using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using ArteryCore.Patches.Effects;
using ArteryCore.Visuals;

namespace ArteryCore.Patches.HitPressure
{
    /// <summary>
    /// Impact shock: every hit the local player takes stacks a dark screen
    /// vignette and a brief pain effect, so being shot at is disorienting even
    /// when the round is stopped by armor.
    /// </summary>
    public sealed class ApplyHitPressureOnDamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.ReceiveDamage),
                new[]
                {
                    typeof(float),
                    typeof(EBodyPart),
                    typeof(EDamageType),
                    typeof(float),
                    typeof(MaterialType)
                });

        [PatchPostfix]
        private static void ApplyImpactShock(Player __instance, float damage,
            EBodyPart part, EDamageType type, float absorbed)
        {
            if (!ArteryConfig.EnableHitPressure.Value || __instance == null ||
                !__instance.IsYourPlayer || type.IsSelfInflicted() ||
                damage + absorbed <= 0f)
                return;

            float strength = HitPressureVignette.ApplyHitStack();
            bool applied = ApplyPain(__instance.ActiveHealthController, part,
                strength);

            if (ArteryConfig.LogEveryShot.Value)
                ArteryLog.Info(string.Format(
                    "[ImpactShock] {0} {1}: body={2:0.##} armor={3:0.##} " +
                    "strength={4:P0} pain={5}",
                    part, type, damage, absorbed, strength,
                    applied ? "applied" : "failed"));
        }

        private static bool ApplyPain(ActiveHealthController health,
            EBodyPart bodyPart, float strength)
        {
            if (health == null || !health.IsAlive) return false;

            float duration = ArteryConfig.HitPressurePainDuration.Value;
            if (duration <= 0f) return false;

            EBodyPart effectBodyPart = bodyPart == EBodyPart.Common
                ? EBodyPart.Chest : bodyPart;
            ActiveHealthController.Pain pain =
                health.FindExistingEffect<ActiveHealthController.Pain>(
                    effectBodyPart);
            if (pain == null)
                pain = health.AddEffect<ActiveHealthController.Pain>(
                    effectBodyPart, 0f, duration, 0f, strength);
            else
            {
                pain.AddWorkTime(duration, true);
                if (strength > pain.Strength) pain.SetStrength(strength);
            }

            if (pain == null) return false;
            NativeEffectLabels.MarkImpactShock(pain, duration);
            return true;
        }
    }
}
