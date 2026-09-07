using System.Reflection;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace ArteryCore.Patches.Bleeding
{
    /// <summary>
    /// Vanilla refuses to kill on a body part destroyed by bleeding damage.
    /// The check at the top of TryToKillAfterDestroyPart is:
    ///
    ///     if (IsBleeding(damageType)) return;
    ///     if (bodyPart == Head || bodyPart == Chest) Kill(damageType);
    ///
    /// so bleeding can drive the chest to zero and the victim simply stands
    /// there with a destroyed chest, forever. That is fine for vanilla, where
    /// bleeds are a slow tax you are meant to survive and bandage. It is fatal
    /// to ArteryCore, whose entire premise is bleeding out.
    ///
    /// This restores the obvious outcome, but only for a target ArteryCore is
    /// actually simulating with an open arterial wound. An ordinary vanilla
    /// bleed on anyone else still behaves exactly as the base game intends.
    /// </summary>
    public sealed class ArterialBleedOutDeathPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController),
                nameof(ActiveHealthController.TryToKillAfterDestroyPart));

        [PatchPrefix]
        private static bool KillOnArterialBleedOut(
            ActiveHealthController __instance, EBodyPart bodyPart,
            EDamageType damageType)
        {
            if (!ArteryConfig.EnableArteries.Value ||
                __instance == null || !__instance.IsAlive)
                return true;

            // Losing anything but the head or the chest is survivable, and
            // non-bleeding damage already kills through the vanilla path.
            if (bodyPart != EBodyPart.Head && bodyPart != EBodyPart.Chest)
                return true;
            if (!damageType.IsBleeding())
                return true;

            Player player = __instance.Player;
            if (player == null || !ArteryConfig.IsTargetEnabled(player))
                return true;

            ArteryController artery = player.GetComponent<ArteryController>();
            if (artery == null || artery.ArterialWoundCount <= 0)
                return true;

            ArteryLog.Info(
                $"[Artery] {bodyPart} destroyed by arterial bleeding on " +
                $"{(player.IsAI ? "AI" : "player")}: bled out");
            __instance.Kill(damageType);
            return false;
        }
    }
}
