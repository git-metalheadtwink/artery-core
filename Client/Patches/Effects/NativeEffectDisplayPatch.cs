using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace ArteryCore.Patches.Effects
{
    /// <summary>
    /// ArteryCore drives real vanilla health effects, so the health panel needs
    /// to be told what each one actually represents now. This tags the effects
    /// ArteryCore owns and rewrites their tooltip text.
    /// </summary>
    internal static class NativeEffectLabels
    {
        private sealed class LabelState
        {
            internal float BruiseExpires;
            internal float ImpactShockExpires;
            internal float ArterialExpires;
            internal float ArterialDamagePerSecond;
            internal ArteryZoneKind ArterialZone;
        }

        private static readonly ConditionalWeakTable<IHealthEffect, LabelState>
            Labels = new ConditionalWeakTable<IHealthEffect, LabelState>();

        internal static void MarkBruised(IHealthEffect effect, float duration)
        {
            if (effect == null) return;
            LabelState labels = Labels.GetOrCreateValue(effect);
            labels.BruiseExpires = Mathf.Max(labels.BruiseExpires,
                Time.unscaledTime + Mathf.Max(0f, duration));
        }

        internal static void MarkImpactShock(IHealthEffect effect,
            float duration)
        {
            if (effect == null) return;
            LabelState labels = Labels.GetOrCreateValue(effect);
            labels.ImpactShockExpires = Mathf.Max(labels.ImpactShockExpires,
                Time.unscaledTime + Mathf.Max(0f, duration));
        }

        internal static void UpdateArterialBleed(IHealthEffect effect,
            float timeLeft, float damagePerSecond, ArteryZoneKind zone)
        {
            if (effect == null) return;
            LabelState labels = Labels.GetOrCreateValue(effect);
            labels.ArterialExpires = Time.unscaledTime + Mathf.Max(0f, timeLeft);
            labels.ArterialDamagePerSecond = Mathf.Max(0f, damagePerSecond);
            labels.ArterialZone = zone;
        }

        internal static List<SimpleBuffDescription> BuildArterialLabels(
            IHealthEffect effect)
        {
            if (effect == null ||
                !Labels.TryGetValue(effect, out LabelState labels) ||
                labels.ArterialExpires <= Time.unscaledTime)
                return null;

            string zoneName = ArteryZones.GetDisplayName(labels.ArterialZone);
            string title = labels.ArterialZone == ArteryZoneKind.None
                ? "Arterial bleeding"
                : "Arterial bleeding (" + zoneName + ")";
            return new List<SimpleBuffDescription>
            {
                BuildTimedLabel(title, labels.ArterialExpires),
                new SimpleBuffDescription(
                    $"Blood loss: {labels.ArterialDamagePerSecond:0.0} HP/s")
            };
        }

        internal static List<SimpleBuffDescription> BuildPainLabels(
            IHealthEffect effect)
        {
            if (effect == null ||
                !Labels.TryGetValue(effect, out LabelState labels))
                return null;

            float now = Time.unscaledTime;
            List<SimpleBuffDescription> active =
                new List<SimpleBuffDescription>(2);
            if (labels.BruiseExpires > now)
                active.Add(BuildTimedLabel("Bruised", labels.BruiseExpires));
            if (labels.ImpactShockExpires > now)
                active.Add(BuildTimedLabel("Impact shock",
                    labels.ImpactShockExpires));
            return active.Count > 0 ? active : null;
        }

        private static BuffDescription BuildTimedLabel(string text,
            float expires)
        {
            if (float.IsInfinity(expires))
                return new BuffDescription(text, () => 0f,
                    float.PositiveInfinity);
            float duration = Mathf.Max(0.01f, expires - Time.unscaledTime);
            return new BuffDescription(text,
                () => duration - Mathf.Max(0f, expires - Time.unscaledTime),
                duration);
        }
    }

    public sealed class NativeEffectDisplayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(HealthHelper),
                nameof(HealthHelper.GetDisplayVariation));

        [PatchPostfix]
        private static void RelabelEffect(IHealthEffect effect,
            ref EffectDescription[] __result)
        {
            if (effect == null || __result == null) return;

            List<SimpleBuffDescription> labels = null;
            if (effect is IFracture &&
                (effect.BodyPart == EBodyPart.Chest ||
                 effect.BodyPart == EBodyPart.Stomach))
                labels = new List<SimpleBuffDescription>
                {
                    new SimpleBuffDescription("Spinal fracture")
                };
            else if (effect is IHeavyBleeding)
                labels = NativeEffectLabels.BuildArterialLabels(effect);
            else if (effect is IPain)
                labels = NativeEffectLabels.BuildPainLabels(effect);

            if (labels == null) return;
            for (int i = 0; i < __result.Length; i++)
                __result[i]?.Replace(new List<SimpleBuffDescription>(labels));
        }
    }
}
