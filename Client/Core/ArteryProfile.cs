using EFT;
using UnityEngine;

namespace ArteryCore
{
    /// <summary>
    /// One complete set of arterial-bleeding tuning values. Humans and AI get
    /// separate profiles, because numbers that make a bot bleed out in five
    /// seconds are unplayable when they are pointed back at the player.
    /// PMC bots and scavs both inherit the AI profile and differ only by a
    /// blood-loss multiplier.
    /// </summary>
    internal readonly struct ArteryProfile
    {
        internal readonly string Name;

        /// <summary>Total blood loss as a multiple of post-armor damage.</summary>
        internal readonly float BleedBudget;

        /// <summary>Seconds an untreated arterial wound bleeds before clotting.</summary>
        internal readonly float BleedDuration;

        /// <summary>0 = flat rate, 1 = most of the loss in the first seconds.</summary>
        internal readonly float FrontLoad;

        /// <summary>
        /// Share of every arterial tick that drains straight to the chest,
        /// bypassing the wounded limb and the stomach. This is what models
        /// bleeding out from blood volume rather than from a limb running out
        /// of hit points, and it is the only route by which a limb artery can
        /// reach a pool that kills.
        /// </summary>
        internal readonly float SystemicFraction;

        /// <summary>
        /// Extra bleed rate per additional arterial wound on the same target.
        /// The whole stack escalates, not just the newest wound, so severing a
        /// second and third artery compounds instead of merely adding.
        /// </summary>
        internal readonly float EscalationPerWound;

        /// <summary>Ceiling on the escalation multiplier.</summary>
        internal readonly float MaximumEscalation;

        internal readonly int StackCap;

        /// <summary>How much of a wound a vanilla bandage closes. 1 = all.</summary>
        internal readonly float TreatmentClotProgress;

        /// <summary>Scales the per-zone severance chance for this target class.</summary>
        internal readonly float ChanceMultiplier;

        /// <summary>Flat multiplier on all arterial blood loss.</summary>
        internal readonly float DamageMultiplier;

        internal ArteryProfile(string name, float bleedBudget,
            float bleedDuration, float frontLoad, float systemicFraction,
            float escalationPerWound, float maximumEscalation, int stackCap,
            float treatmentClotProgress, float chanceMultiplier,
            float damageMultiplier)
        {
            Name = name;
            BleedBudget = Mathf.Max(0f, bleedBudget);
            BleedDuration = Mathf.Max(0.01f, bleedDuration);
            FrontLoad = Mathf.Clamp01(frontLoad);
            SystemicFraction = Mathf.Clamp01(systemicFraction);
            EscalationPerWound = Mathf.Max(0f, escalationPerWound);
            MaximumEscalation = Mathf.Max(1f, maximumEscalation);
            StackCap = Mathf.Max(1, stackCap);
            TreatmentClotProgress = Mathf.Clamp01(treatmentClotProgress);
            ChanceMultiplier = Mathf.Max(0f, chanceMultiplier);
            DamageMultiplier = Mathf.Max(0f, damageMultiplier);
        }

        /// <summary>
        /// Escalation multiplier for a target currently carrying
        /// <paramref name="activeWounds"/> arterial wounds anywhere on the body.
        /// </summary>
        internal float GetEscalation(int activeWounds)
        {
            if (activeWounds <= 1 || EscalationPerWound <= 0f) return 1f;
            return Mathf.Min(MaximumEscalation,
                1f + EscalationPerWound * (activeWounds - 1));
        }

        /// <summary>
        /// Area under this profile's decay curve, used to turn a total
        /// blood-loss budget into a starting rate.
        /// </summary>
        internal float GetDecayArea() =>
            ArteryConfig.GetBleedDecayArea(BleedDuration, FrontLoad);

        internal float GetStrength(float elapsed) =>
            ArteryConfig.GetBleedStrength(elapsed, BleedDuration, FrontLoad);

        /// <summary>
        /// How much a single unit of budget actually delivers once it has been
        /// split between the systemic route and the local linkage route. The
        /// budget is divided by this so the configured total blood loss stays
        /// honest no matter how the routing is configured.
        /// </summary>
        internal float GetDeliveryMultiplier(float localMultiplier) =>
            Mathf.Max(0.01f, SystemicFraction +
                (1f - SystemicFraction) * Mathf.Max(0f, localMultiplier));

        internal static ArteryProfile Resolve(Player player)
        {
            if (player == null || !player.IsAI)
                return ArteryConfig.BuildPlayerProfile();
            return ArteryConfig.BuildAiProfile(
                player.Side == EPlayerSide.Savage);
        }
    }
}
