using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace ArteryCore.Patches.Bleeding
{
    /// <summary>
    /// A kill that lands seconds later from an arterial bleed reaches the
    /// statistics manager with the bleed as the cause, which loses the weapon,
    /// the body part and the distance. Quest conditions read exactly those
    /// fields, so the last real weapon hit on each victim is cached and
    /// substituted back in when they die.
    /// </summary>
    internal static class BleedKillAttribution
    {
        private readonly struct WeaponHit
        {
            internal readonly string AggressorProfileId;
            internal readonly DamageInfo DamageInfo;
            internal readonly EBodyPart BodyPart;
            internal readonly float Distance;

            internal WeaponHit(string aggressorProfileId, DamageInfo damageInfo,
                EBodyPart bodyPart, float distance)
            {
                AggressorProfileId = aggressorProfileId;
                DamageInfo = damageInfo;
                BodyPart = bodyPart;
                Distance = distance;
            }
        }

        private static readonly Dictionary<string, WeaponHit> HitByVictim =
            new Dictionary<string, WeaponHit>();

        internal static void Capture(Player victim, DamageInfo damageInfo,
            EBodyPart bodyPart)
        {
            if (victim == null || victim.Profile == null ||
                !damageInfo.HaveOwner ||
                damageInfo.Player.iPlayer.ProfileId == victim.Profile.Id ||
                !damageInfo.DamageType.IsWeaponInduced())
                return;

            float distance = Vector3.Distance(
                damageInfo.Player.iPlayer.Position, victim.Position);
            HitByVictim[victim.Profile.Id] = new WeaponHit(
                damageInfo.Player.iPlayer.ProfileId, damageInfo, bodyPart,
                distance);
        }

        internal static bool TryApply(string victimProfileId,
            ref DamageInfo damageInfo, ref EBodyPart bodyPart,
            ref float distance)
        {
            if (string.IsNullOrEmpty(victimProfileId) ||
                !HitByVictim.TryGetValue(victimProfileId, out WeaponHit hit))
                return false;

            // Only substitute when the credited aggressor is the same shooter,
            // so a third party finishing the victim keeps their own credit.
            string creditedAggressorId = damageInfo.HaveOwner
                ? damageInfo.Player.iPlayer.ProfileId : null;
            if (!string.IsNullOrEmpty(creditedAggressorId) &&
                creditedAggressorId != hit.AggressorProfileId)
                return false;

            damageInfo = hit.DamageInfo;
            bodyPart = hit.BodyPart;
            distance = hit.Distance;
            HitByVictim.Remove(victimProfileId);
            return true;
        }

        internal static void Clear() { HitByVictim.Clear(); }

        /// <summary>
        /// Prefix on every declared Player.ApplyDamageInfo implementation.
        /// Applied by VirtualPlayerPatches because ApplyDamageInfo is virtual
        /// and co-op frameworks override it.
        /// </summary>
        internal static void CaptureHit(Player __instance,
            DamageInfo damageInfo, EBodyPart bodyPartType)
        {
            if (!ArteryConfig.EnableBleedKillCredit.Value) return;
            Capture(__instance, damageInfo, bodyPartType);
        }
    }

    public sealed class ApplyBleedKillAttributionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BaseStatisticsManager),
                nameof(BaseStatisticsManager.OnEnemyKill));

        [PatchPrefix]
        private static void RestoreWeaponHit(ref DamageInfo damage,
            ref EBodyPart bodyPart, string playerProfileId, ref float distance)
        {
            if (!ArteryConfig.EnableBleedKillCredit.Value) return;
            BleedKillAttribution.TryApply(playerProfileId, ref damage,
                ref bodyPart, ref distance);
        }
    }
}
