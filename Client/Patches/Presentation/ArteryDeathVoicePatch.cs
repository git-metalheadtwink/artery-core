using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace ArteryCore.Patches.Presentation
{
    /// <summary>
    /// Bleeding out reads differently from being shot dead. When the killing
    /// tick came from an arterial bleed, favour the agony line over the plain
    /// death line.
    ///
    /// Applied by VirtualPlayerPatches to every declared Player.OnDead
    /// implementation, because OnDead is virtual and co-op frameworks
    /// override it.
    /// </summary>
    internal static class ArteryDeathVoice
    {
        private const float AgonyChance = 0.75f;

        [ThreadStatic] private static bool _replaceNativeVoice;
        [ThreadStatic] private static EPhraseTrigger _replacementPhrase;

        internal static bool ReplacingNativeVoice => _replaceNativeVoice;

        internal static void ChooseDeathPhrase(Player __instance)
        {
            if (!ArteryConfig.EnablePresentation.Value || __instance == null ||
                __instance.Speaker == null)
                return;

            ArteryController artery =
                __instance.GetComponent<ArteryController>();
            if (artery == null || !artery.BleedDeathVoicePending) return;

            _replaceNativeVoice = true;
            _replacementPhrase = UnityEngine.Random.value < AgonyChance
                ? EPhraseTrigger.OnAgony
                : EPhraseTrigger.OnDeath;
        }

        internal static void PlayDeathPhrase(Player __instance)
        {
            if (!_replaceNativeVoice || __instance == null ||
                __instance.Speaker == null) return;
            try
            {
                __instance.Speaker.Play(_replacementPhrase,
                    __instance.HealthStatus, true, null);
            }
            catch (Exception exception)
            {
                ArteryLog.Error(
                    "[DeathVoice] Could not play the replacement phrase: " +
                    exception);
            }
        }

        internal static void ResetState()
        {
            _replaceNativeVoice = false;
            _replacementPhrase = EPhraseTrigger.None;
        }
    }

    /// <summary>
    /// ShouldVocalizeDeath is virtual, but only remote-player proxies override
    /// it, and ArteryCore never simulates those. Patching the base is enough.
    /// </summary>
    public sealed class SuppressReplacedDeathVoicePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player),
                nameof(Player.ShouldVocalizeDeath));

        [PatchPostfix]
        private static void SuppressNativeVoice(ref bool __result)
        {
            if (ArteryDeathVoice.ReplacingNativeVoice) __result = false;
        }
    }
}
