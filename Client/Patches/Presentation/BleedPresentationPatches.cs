using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace ArteryCore.Patches.Presentation
{
    /// <summary>
    /// ArteryCore ticks arterial blood loss 60 times a second. Left alone that
    /// would fire the hurt grunt and the screen-blood splash on every tick, so
    /// the controller marks when a tick is allowed to present itself.
    /// </summary>
    internal static class BleedPresentationContext
    {
        [ThreadStatic] internal static bool InsideBleedDamage;
        [ThreadStatic] internal static bool AllowPresentation;

        internal static bool ShouldSuppress() =>
            ArteryConfig.EnablePresentation.Value &&
            InsideBleedDamage && !AllowPresentation;
    }

    public sealed class BleedVoiceThrottlePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player),
                nameof(Player.OnAudioHealthApplyDamage));

        [PatchPrefix]
        private static bool ThrottleVoice() =>
            !BleedPresentationContext.ShouldSuppress();
    }

    public sealed class BleedScreenBloodThrottlePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EffectsController),
                nameof(EffectsController.OnHealthApplyDamage));

        [PatchPrefix]
        private static bool ThrottleScreenBlood() =>
            !BleedPresentationContext.ShouldSuppress();
    }
}
