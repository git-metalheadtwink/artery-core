using System;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace ArteryCore.Patches.Shot
{
    /// <summary>
    /// Reads the damage a bullet has left after armor, so the arterial
    /// blood-loss budget can be sized from it. Strictly read-only: ArteryCore
    /// deliberately never rescales the damage of the bullet itself, which is
    /// what keeps non-artery hits identical to vanilla.
    /// </summary>
    internal static class PostArmorDamageProbe
    {
        [ThreadStatic] private static bool _armed;
        [ThreadStatic] private static bool _captured;
        [ThreadStatic] private static float _postArmorDamage;
        [ThreadStatic] private static Player _targetPlayer;

        internal static void Arm(Player targetPlayer)
        {
            _armed = true;
            _captured = false;
            _postArmorDamage = 0f;
            _targetPlayer = targetPlayer;
        }

        internal static void Capture(Player targetPlayer, float damage)
        {
            if (!_armed || targetPlayer != _targetPlayer) return;
            _postArmorDamage = Mathf.Max(0f, damage);
            _captured = true;
            _armed = false;
        }

        internal static bool TryTake(Player targetPlayer, out float damage)
        {
            damage = 0f;
            if (!_captured || targetPlayer != _targetPlayer) return false;
            damage = _postArmorDamage;
            Clear();
            return true;
        }

        internal static void Clear()
        {
            _armed = false;
            _captured = false;
            _postArmorDamage = 0f;
            _targetPlayer = null;
        }
    }

    public sealed class PostArmorDamageProbePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player),
                nameof(Player.ProceedDamageThroughArmor),
                new[]
                {
                    typeof(DamageInfo).MakeByRefType(),
                    typeof(EBodyPartColliderType),
                    typeof(EArmorPlateCollider),
                    typeof(bool)
                });

        [PatchPostfix]
        private static void CapturePostArmorDamage(Player __instance,
            ref DamageInfo damageInfo)
        {
            PostArmorDamageProbe.Capture(__instance, damageInfo.Damage);
        }
    }
}
