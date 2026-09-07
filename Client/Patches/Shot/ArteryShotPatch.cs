using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using UnityEngine;

namespace ArteryCore.Patches.Shot
{
    /// <summary>
    /// The single entry point of ArteryCore. Classifies every bullet impact
    /// against the artery collider table and the bone capsules, then applies
    /// arterial bleeding, fractures, bruising and blood on top of whatever
    /// vanilla already did. The damage of the bullet itself is never modified.
    ///
    /// Applied by <see cref="VirtualPlayerPatches"/> to every declared
    /// ApplyShot implementation, not just the one on EFT.Player, because
    /// ApplyShot is virtual and co-op frameworks override it.
    /// </summary>
    internal static class ArteryShotPatch
    {
        private enum BoneKind { Limb, CervicalSpine, ThoracicSpine }

        private const float LimbResistanceDepth = 0.05f;
        private const float LimbVelocityRetention = 0.90f;
        private const float CervicalResistanceDepth = 0.12f;
        private const float CervicalVelocityRetention = 0.75f;
        private const float ThoracicResistanceDepth = 0.14f;
        private const float ThoracicVelocityRetention = 0.72f;

        private readonly struct BoneCollision
        {
            internal readonly BoneKind Kind;
            internal readonly float Distance;

            internal BoneCollision(BoneKind kind, Vector3 point,
                Vector3 hitPoint)
            {
                Kind = kind;
                Distance = Vector3.Distance(hitPoint, point);
            }
        }

        internal struct HitState
        {
            public bool Processed, CorpseShot;
            public bool LimbBone, CervicalSpine, ThoracicSpine;
            public EBodyPart BodyPart;
            public EBodyPartColliderType Collider;
            public float OriginalDamage;
            public int FireIndex, ProjectileIndex;
            public Vector3 HitPoint, HitNormal, Direction;
            public Transform HitTransform;
            public WoundBallistics Wound;
        }

        internal static void ClassifyShot(Player __instance,
            ref DamageInfo damageInfo, EBodyPart bodyPartType,
            EBodyPartColliderType colliderType, ShotId shotId,
            ref HitState __state)
        {
            DeterministicFracturePatch.InsideShot = false;
            PostArmorDamageProbe.Clear();

            if (__instance == null || bodyPartType == EBodyPart.Common ||
                damageInfo.Damage <= 0f ||
                !ArteryConfig.IsTargetEnabled(__instance))
                return;

            // A player that is neither yours nor AI is a remote human in a
            // co-op session. Their health is authoritative on their own
            // client, which is running its own copy of ArteryCore, so
            // simulating them here would double up every arterial wound.
            if (!__instance.IsYourPlayer && !__instance.IsAI) return;

            bool wantsArteries = ArteryConfig.EnableArteries.Value;
            bool wantsFractures = ArteryConfig.EnableBoneFractures.Value;
            bool wantsBruising = ArteryConfig.EnableBruising.Value;
            if (!wantsArteries && !wantsFractures && !wantsBruising) return;

            try
            {
                __state.Processed = true;
                __state.BodyPart = bodyPartType;
                __state.Collider = colliderType;
                __state.OriginalDamage = damageInfo.Damage;
                __state.HitPoint = damageInfo.HitPoint;
                __state.HitNormal = damageInfo.HitNormal;
                __state.Direction = damageInfo.Direction;
                __state.FireIndex = damageInfo.FireIndex;
                __state.ProjectileIndex = shotId._fragmentIndex;
                if (damageInfo.HitCollider != null)
                    __state.HitTransform =
                        damageInfo.HitCollider.attachedRigidbody != null
                            ? damageInfo.HitCollider.attachedRigidbody.transform
                            : damageInfo.HitCollider.transform;

                // Already stopped by an earlier armor layer: the postfix will
                // turn this into a bruise, nothing else to measure.
                if (IsStoppedByArmor(damageInfo)) return;

                // Read the post-armor damage without changing it. Needed to
                // size the arterial blood-loss budget.
                PostArmorDamageProbe.Arm(__instance);

                if (wantsFractures) DeterministicFracturePatch.InsideShot = true;

                // Armor that is only just penetrated still robs the bullet of
                // penetration power, which shortens how deep it reaches.
                DamageInfo woundDamageInfo = damageInfo;
                if (damageInfo.HittedBallisticCollider is BodyPartCollider
                        bodyPartCollider &&
                    __instance.TryGetArmorResistData(bodyPartCollider,
                        damageInfo.PenetrationPower,
                        out ArmorResistanceData resistance))
                    woundDamageInfo.PenetrationPower *= resistance.CF;

                __state.Wound = WoundBallistics.Evaluate(__instance,
                    bodyPartType, woundDamageInfo);

                if (__instance.ActiveHealthController != null &&
                    !__instance.ActiveHealthController.IsAlive)
                {
                    __state.CorpseShot = true;
                    return;
                }

                if (wantsFractures)
                    ResolveBoneCollisions(__instance, damageInfo, ref __state);
            }
            catch (Exception exception)
            {
                ArteryLog.Error("[ArteryShot] Classification failed: " +
                    exception);
            }
        }

        /// <summary>
        /// Walks the bone capsules the bullet path crosses, nearest first, and
        /// spends penetration depth on each one it can still reach.
        /// </summary>
        private static void ResolveBoneCollisions(Player player,
            DamageInfo damageInfo, ref HitState state)
        {
            List<BoneCollision> collisions = new List<BoneCollision>(3);

            if (ArteryConfig.FractureSpine.Value)
            {
                if ((state.BodyPart == EBodyPart.Head ||
                     state.BodyPart == EBodyPart.Chest) &&
                    BoneGeometry.IntersectsCervicalSpine(player,
                        damageInfo.HitPoint, damageInfo.Direction,
                        out Vector3 cervical))
                    collisions.Add(new BoneCollision(BoneKind.CervicalSpine,
                        cervical, damageInfo.HitPoint));

                if ((state.BodyPart == EBodyPart.Chest ||
                     state.BodyPart == EBodyPart.Stomach) &&
                    BoneGeometry.IntersectsThoracicSpine(player,
                        damageInfo.HitPoint, damageInfo.Direction,
                        out Vector3 thoracic))
                    collisions.Add(new BoneCollision(BoneKind.ThoracicSpine,
                        thoracic, damageInfo.HitPoint));
            }

            if (ArteryConfig.FractureLimbBones.Value &&
                (BoneGeometry.IsArm(state.BodyPart) ||
                 BoneGeometry.IsLeg(state.BodyPart)) &&
                BoneGeometry.IntersectsLimbBone(player, state.BodyPart,
                    damageInfo.HitPoint, damageInfo.Direction,
                    out Vector3 limb))
                collisions.Add(new BoneCollision(BoneKind.Limb, limb,
                    damageInfo.HitPoint));

            if (collisions.Count == 0) return;
            collisions.Sort((left, right) =>
                left.Distance.CompareTo(right.Distance));

            for (int i = 0; i < collisions.Count; i++)
            {
                BoneCollision collision = collisions[i];
                if (!state.Wound.CanReach(collision.Distance)) continue;

                switch (collision.Kind)
                {
                    case BoneKind.Limb:
                        state.LimbBone = true;
                        state.Wound = state.Wound.ApplyBoneResistance(
                            damageInfo.Direction, collision.Distance,
                            LimbResistanceDepth, LimbVelocityRetention);
                        break;
                    case BoneKind.CervicalSpine:
                        state.CervicalSpine = true;
                        state.Wound = state.Wound.ApplyBoneResistance(
                            damageInfo.Direction, collision.Distance,
                            CervicalResistanceDepth,
                            CervicalVelocityRetention);
                        break;
                    case BoneKind.ThoracicSpine:
                        state.ThoracicSpine = true;
                        state.Wound = state.Wound.ApplyBoneResistance(
                            damageInfo.Direction, collision.Distance,
                            ThoracicResistanceDepth,
                            ThoracicVelocityRetention);
                        break;
                }
            }
        }

        internal static void ApplyShotResults(Player __instance,
            ref DamageInfo damageInfo, HitState __state)
        {
            DeterministicFracturePatch.InsideShot = false;
            if (!__state.Processed || __instance == null ||
                __instance.ActiveHealthController == null)
                return;

            try
            {
                bool armorStopped = IsStoppedByArmor(damageInfo);
                ArteryController artery = ArteryController.GetOrCreate(__instance);
                artery.CaptureImpact(__state.HitPoint, __state.Direction,
                    __state.HitTransform);

                if (armorStopped)
                {
                    artery.AddBruise(__state.OriginalDamage);
                    if (ArteryConfig.LogEveryShot.Value)
                        ArteryLog.Info("[ArteryShot] " + __state.Collider +
                            ": armor stopped, bruise only");
                    return;
                }

                artery.PaintBloodAtHit(__state.HitPoint, __state.HitNormal);

                if (__state.CorpseShot)
                {
                    artery.AddCorpseWound(__state.BodyPart);
                    return;
                }

                ApplyFractures(__instance, __state);
                ApplyArtery(__instance, artery, __state, damageInfo);
            }
            catch (Exception exception)
            {
                ArteryLog.Error("[ArteryShot] Application failed: " + exception);
            }
            finally
            {
                PostArmorDamageProbe.Clear();
            }
        }

        private static void ApplyArtery(Player player, ArteryController artery,
            HitState state, DamageInfo damageInfo)
        {
            if (!ArteryConfig.EnableArteries.Value) return;

            // A hit-level bleed blocker means this shot must not bleed at all.
            if (damageInfo.BleedBlock)
            {
                if (ArteryConfig.LogEveryShot.Value)
                    ArteryLog.Info("[ArteryShot] " + state.Collider +
                        ": bleed blocked by hit flags");
                return;
            }

            ArteryProfile profile = ArteryProfile.Resolve(player);
            ArteryZones.Roll roll = ArteryZones.Evaluate(state.Collider,
                state.Wound.DepthRatio, state.FireIndex,
                state.ProjectileIndex, state.HitPoint, profile);

            if (ArteryConfig.LogEveryShot.Value)
                ArteryLog.Info(string.Format(
                    "[ArteryShot] {0} ({1}) profile={2} zone={3} depth={4:P0} " +
                    "pen={5:F3}m thickness={6:F3}m {7} | roll={8:F3} vs {9:P0} " +
                    "=> {10}",
                    state.Collider, state.BodyPart, profile.Name,
                    ArteryZones.GetDisplayName(roll.Kind),
                    state.Wound.DepthRatio, state.Wound.PenetrationDepth,
                    state.Wound.ReferenceThickness,
                    state.Wound.PassedThrough ? "THROUGH" : "STOPPED",
                    roll.Value, roll.Chance,
                    roll.Severed ? "SEVERED"
                        : roll.DepthGated ? "too shallow" : "missed"));

            if (!roll.Severed) return;

            float postArmorDamage = state.OriginalDamage;
            if (PostArmorDamageProbe.TryTake(player, out float captured))
                postArmorDamage = captured;

            // state.BodyPart is the pool EFT itself resolved for this collider.
            // The collider-to-body-part binding is serialised in the player
            // prefab, not in managed code, so it must never be hard-coded.
            artery.AddArterialWound(state.BodyPart, roll.Kind, postArmorDamage,
                state.Wound.ArterialSeverityScale, state.Wound.PassedThrough,
                damageInfo.HaveOwner ? damageInfo.Player : null);
        }

        private static void ApplyFractures(Player player, HitState state)
        {
            if (!ArteryConfig.EnableBoneFractures.Value) return;
            ActiveHealthController health = player.ActiveHealthController;
            if (health == null || !health.IsAlive) return;

            if (state.LimbBone && !HasFracture(health, state.BodyPart))
            {
                DeterministicFracturePatch.AllowNextFracture = true;
                try { health.DoFracture(state.BodyPart); }
                finally { DeterministicFracturePatch.AllowNextFracture = false; }
                AddFracturePain(health, state.BodyPart);
                ArteryLog.Info("[Bone] " + state.BodyPart +
                    " bone struck: fracture applied");
            }

            if (!state.CervicalSpine && !state.ThoracicSpine) return;

            EBodyPart spinalPart = state.BodyPart == EBodyPart.Stomach
                ? EBodyPart.Stomach : EBodyPart.Chest;
            if (HasFracture(health, spinalPart)) return;

            health.AddEffect<ActiveHealthController.Fracture>(spinalPart, 0f,
                null, null, null);
            AddFracturePain(health, spinalPart);
            ArteryLog.Info("[Bone] Spine struck: " + spinalPart +
                " spinal fracture applied");
        }

        private static void AddFracturePain(ActiveHealthController health,
            EBodyPart bodyPart)
        {
            float strength = ArteryConfig.FracturePainStrength.Value;
            float duration = ArteryConfig.FracturePainDuration.Value;
            if (strength <= 0f || duration <= 0f) return;

            ActiveHealthController.Pain pain =
                health.FindExistingEffect<ActiveHealthController.Pain>(bodyPart);
            if (pain == null)
                health.AddEffect<ActiveHealthController.Pain>(bodyPart, 0f,
                    duration, 0f, strength);
            else
            {
                pain.AddWorkTime(duration, true);
                if (strength > pain.Strength) pain.SetStrength(strength);
            }
        }

        private static bool HasFracture(ActiveHealthController health,
            EBodyPart bodyPart)
        {
            foreach (IHealthEffect effect in health.GetAllActiveEffects())
                if (effect is IFracture && effect.BodyPart == bodyPart)
                    return true;
            return false;
        }

        private static bool IsStoppedByArmor(DamageInfo damageInfo) =>
            damageInfo.BlockedBy.HasValue || damageInfo.DeflectedBy.HasValue;

        internal static Exception ResetShotState(Exception __exception)
        {
            DeterministicFracturePatch.InsideShot = false;
            DeterministicFracturePatch.AllowNextFracture = false;
            PostArmorDamageProbe.Clear();
            return __exception;
        }
    }
}
