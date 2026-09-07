using EFT;
using UnityEngine;

namespace ArteryCore
{
    /// <summary>
    /// The major superficial arteries ArteryCore models. Each kind covers one
    /// or more EBodyPartColliderType colliders, left and right sharing settings.
    /// </summary>
    internal enum ArteryZoneKind
    {
        None = 0,
        Carotid,
        Brachial,
        Radial,
        Femoral,
        FemoralGroin
    }

    /// <summary>
    /// Maps the EFT collider that was actually struck onto an artery zone, and
    /// rolls the per-bullet severance chance.
    /// </summary>
    internal static class ArteryZones
    {
        /// <summary>
        /// Which artery, if any, runs through the given collider. This is the
        /// whole of artery detection: ArteryCore is collider-gated, so any
        /// penetrating hit on one of these colliders can sever the artery,
        /// subject to the depth gate and the per-zone chance roll.
        /// </summary>
        internal static ArteryZoneKind Resolve(EBodyPartColliderType collider)
        {
            switch (collider)
            {
                // Verified in-game: the neck colliders resolve to
                // EBodyPart.Chest, not Head. Never assume otherwise here --
                // the collider-to-pool binding lives in the player prefab.
                case EBodyPartColliderType.NeckFront:
                case EBodyPartColliderType.NeckBack:
                    return ArteryZoneKind.Carotid;

                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm:
                    return ArteryZoneKind.Brachial;

                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm:
                    return ArteryZoneKind.Radial;

                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh:
                    return ArteryZoneKind.Femoral;

                case EBodyPartColliderType.Pelvis:
                case EBodyPartColliderType.PelvisBack:
                    return ArteryZoneKind.FemoralGroin;

                default:
                    return ArteryZoneKind.None;
            }
        }

        internal static string GetDisplayName(ArteryZoneKind kind)
        {
            switch (kind)
            {
                case ArteryZoneKind.Carotid: return "carotid";
                case ArteryZoneKind.Brachial: return "brachial";
                case ArteryZoneKind.Radial: return "radial";
                case ArteryZoneKind.Femoral: return "femoral";
                case ArteryZoneKind.FemoralGroin: return "femoral (groin)";
                default: return "none";
            }
        }

        /// <summary>
        /// Result of testing one bullet against one artery zone.
        /// </summary>
        internal readonly struct Roll
        {
            internal readonly ArteryZoneKind Kind;
            internal readonly bool Severed;
            internal readonly float Chance;
            internal readonly float Value;
            internal readonly bool DepthGated;

            internal Roll(ArteryZoneKind kind, bool severed, float chance,
                float value, bool depthGated)
            {
                Kind = kind;
                Severed = severed;
                Chance = chance;
                Value = value;
                DepthGated = depthGated;
            }

            internal static Roll Miss(ArteryZoneKind kind) =>
                new Roll(kind, false, 0f, 1f, false);
        }

        /// <summary>
        /// Decides whether this bullet severed the artery in the struck
        /// collider. The roll is seeded from the shot identity and the impact
        /// point so it is stable if the shot is evaluated more than once, while
        /// still varying freely between shots and between shotgun pellets.
        /// </summary>
        internal static Roll Evaluate(EBodyPartColliderType collider,
            float woundDepthRatio, int fireIndex, int projectileIndex,
            Vector3 hitPoint, in ArteryProfile profile)
        {
            ArteryZoneKind kind = Resolve(collider);
            if (kind == ArteryZoneKind.None ||
                !ArteryConfig.IsZoneEnabled(kind))
                return Roll.Miss(kind);

            if (woundDepthRatio < ArteryConfig.ArteryMinimumDepthRatio.Value)
                return new Roll(kind, false, 0f, 1f, true);

            if (ArteryConfig.ForceArteryHits.Value)
                return new Roll(kind, true, 1f, 0f, false);

            // The zone chance is the base rate; each target class scales it,
            // so AI can be made reliably severable without touching the
            // numbers that apply to human players.
            float chance = Mathf.Clamp01(ArteryConfig.GetZoneChance(kind) *
                profile.ChanceMultiplier);
            if (chance <= 0f)
                return Roll.Miss(kind);
            if (chance >= 1f)
                return new Roll(kind, true, 1f, 0f, false);

            float value = NextUnitValue(BuildSeed(collider, fireIndex,
                projectileIndex, hitPoint));
            return new Roll(kind, value < chance, chance, value, false);
        }

        private static int BuildSeed(EBodyPartColliderType collider,
            int fireIndex, int projectileIndex, Vector3 hitPoint)
        {
            unchecked
            {
                int seed = 17;
                seed = seed * 31 + fireIndex;
                seed = seed * 31 + projectileIndex;
                seed = seed * 31 + (int)collider;
                // Quantise to a millimetre so floating-point jitter between two
                // evaluations of the same impact cannot flip the roll.
                seed = seed * 31 + Mathf.RoundToInt(hitPoint.x * 1000f);
                seed = seed * 31 + Mathf.RoundToInt(hitPoint.y * 1000f);
                seed = seed * 31 + Mathf.RoundToInt(hitPoint.z * 1000f);
                return seed;
            }
        }

        /// <summary>
        /// A cheap, well-mixed hash to a value in [0,1). Avoids allocating a
        /// System.Random per bullet.
        /// </summary>
        private static float NextUnitValue(int seed)
        {
            unchecked
            {
                uint hash = (uint)seed;
                hash ^= 2747636419u;
                hash *= 2654435769u;
                hash ^= hash >> 16;
                hash *= 2654435769u;
                hash ^= hash >> 16;
                hash *= 2654435769u;
                return (hash >> 8) / 16777216f;
            }
        }

    }
}
