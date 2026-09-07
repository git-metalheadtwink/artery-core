using System.Reflection;
using DeferredDecals;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace ArteryCore.Patches.Bleeding
{
    /// <summary>
    /// Arterial bleeding produces far more blood decals than vanilla expects,
    /// and the default budget recycles them almost immediately. Raise the cap
    /// so a bleed trail actually persists.
    /// </summary>
    public sealed class PersistentBloodDecalsPatch : ModulePatch
    {
        private const int PersistentStaticDecalCapacity = 2000;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(DeferredDecalRenderer),
                nameof(DeferredDecalRenderer.Awake));

        [PatchPrefix]
        private static void RaiseDecalCapacity(DeferredDecalRenderer __instance)
        {
            if (!ArteryConfig.EnablePersistentBloodDecals.Value) return;
            __instance._maxDecals = PersistentStaticDecalCapacity;
            ArteryLog.Info("[BloodDecals] Static decal capacity set to " +
                PersistentStaticDecalCapacity);
        }
    }
}
