using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace ArteryCore
{
    /// <summary>
    /// Anatomical bone hitboxes. EFT only ships one collider per limb segment
    /// and no skeleton at all, so ArteryCore lays capsules along the real bone
    /// transforms and tests the bullet path against them. This is the whole of
    /// the ported anatomical-hitbox system; arteries are collider-gated and do
    /// not use any of it beyond the depth reference.
    /// </summary>
    internal static class BoneGeometry
    {
        /// <summary>
        /// How far past the impact point a bullet path is traced when looking
        /// for bone intersections. Beyond this the shot has left the body.
        /// </summary>
        internal const float MaximumTraceLength = 0.55f;

        /// <summary>
        /// Top of the cervical spine, in head-bone local space. Derived from the
        /// base of the skull rather than from any organ volume.
        /// </summary>
        private static readonly Vector3 CervicalHeadEndOffset =
            new Vector3(-0.02252536f, -0.01592078f, 0.007489671f);

        private static readonly Vector3 CervicalChestEndOffset =
            new Vector3(0.002347419f, -0.04225352f, -0.03286385f);
        private static readonly Vector3 SpineChestEndOffset =
            new Vector3(-6.77723E-11f, -0.03990611f, -0.0258216f);
        private static readonly Vector3 SpinePelvisEndOffset =
            new Vector3(-0.02112676f, -0.07981221f, -0.009389671f);

        private static readonly Dictionary<int, LimbBoneCache> LimbCaches =
            new Dictionary<int, LimbBoneCache>();

        internal static void ClearLimbBoneCache() { LimbCaches.Clear(); }

        // ---- rig anchors -----------------------------------------------------

        internal static Transform GetChestAnchor(Player player) =>
            player?.PlayerBones?.Ribcage?.Original;

        internal static Transform GetPelvisAnchor(Player player) =>
            player?.PlayerBones?.Pelvis?.Original;

        internal static Transform GetHeadAnchor(Player player) =>
            player?.PlayerBones?.Head?.Original;

        // ---- spine -----------------------------------------------------------

        internal static bool TryGetCervicalSpineSegment(Player player,
            out Vector3 skullBase, out Vector3 chestTop)
        {
            skullBase = chestTop = Vector3.zero;
            Transform head = GetHeadAnchor(player);
            Transform ribcage = GetChestAnchor(player);
            if (head == null || ribcage == null) return false;

            skullBase = head.TransformPoint(CervicalHeadEndOffset);
            chestTop = ribcage.position +
                ribcage.TransformVector(CervicalChestEndOffset);
            return (skullBase - chestTop).sqrMagnitude > 0.0001f;
        }

        internal static bool IntersectsCervicalSpine(Player player,
            Vector3 hitPoint, Vector3 direction, out Vector3 intersection)
        {
            intersection = hitPoint;
            if (direction.sqrMagnitude < 0.0001f) return false;
            if (!TryGetCervicalSpineSegment(player, out Vector3 skullBase,
                out Vector3 chestTop)) return false;
            Vector3 shotEnd = hitPoint + direction.normalized * MaximumTraceLength;
            return SegmentCapsuleHit(hitPoint, shotEnd, skullBase, chestTop,
                ArteryConfig.CervicalSpineRadius.Value, out intersection);
        }

        internal static bool TryGetThoracicSpineSegment(Player player,
            out Vector3 chestTop, out Vector3 stomachTop)
        {
            chestTop = stomachTop = Vector3.zero;
            Transform ribcage = GetChestAnchor(player);
            Transform pelvis = GetPelvisAnchor(player);
            if (ribcage == null || pelvis == null) return false;

            chestTop = ribcage.position +
                ribcage.TransformVector(SpineChestEndOffset);
            stomachTop = pelvis.position +
                pelvis.TransformVector(SpinePelvisEndOffset);
            return (chestTop - stomachTop).sqrMagnitude > 0.0001f;
        }

        internal static bool IntersectsThoracicSpine(Player player,
            Vector3 hitPoint, Vector3 direction, out Vector3 intersection)
        {
            intersection = hitPoint;
            if (direction.sqrMagnitude < 0.0001f) return false;
            if (!TryGetThoracicSpineSegment(player, out Vector3 chestTop,
                out Vector3 stomachTop)) return false;
            Vector3 shotEnd = hitPoint + direction.normalized * MaximumTraceLength;
            return SegmentCapsuleHit(hitPoint, shotEnd, chestTop, stomachTop,
                ArteryConfig.ThoracicSpineRadius.Value, out intersection);
        }

        // ---- limb bones ------------------------------------------------------

        internal static bool TryGetBoneSegments(Player player,
            EBodyPart bodyPart, out Transform firstStart, out Transform firstEnd,
            out Transform secondStart, out Transform secondEnd)
        {
            firstStart = firstEnd = secondStart = secondEnd = null;
            if (player == null || player.PlayerBones == null) return false;

            int playerId = player.GetInstanceID();
            if (!LimbCaches.TryGetValue(playerId, out LimbBoneCache cache))
            {
                cache = new LimbBoneCache();
                LimbCaches.Add(playerId, cache);
            }
            cache.Resolve(player);

            switch (bodyPart)
            {
                case EBodyPart.LeftArm:
                    firstStart = cache.LeftShoulder; firstEnd = cache.LeftElbow;
                    secondStart = cache.LeftElbow; secondEnd = cache.LeftHand;
                    break;
                case EBodyPart.RightArm:
                    firstStart = cache.RightShoulder; firstEnd = cache.RightElbow;
                    secondStart = cache.RightElbow; secondEnd = cache.RightHand;
                    break;
                case EBodyPart.LeftLeg:
                    firstStart = cache.LeftHip; firstEnd = cache.LeftKnee;
                    secondStart = cache.LeftCalf ?? cache.LeftKnee;
                    secondEnd = cache.LeftFoot;
                    break;
                case EBodyPart.RightLeg:
                    firstStart = cache.RightHip; firstEnd = cache.RightKnee;
                    secondStart = cache.RightCalf ?? cache.RightKnee;
                    secondEnd = cache.RightFoot;
                    break;
            }
            return firstStart != null && firstEnd != null ||
                   secondStart != null && secondEnd != null;
        }

        internal static bool IntersectsLimbBone(Player player,
            EBodyPart bodyPart, Vector3 hitPoint, Vector3 direction,
            out Vector3 intersection)
        {
            intersection = hitPoint;
            if (direction.sqrMagnitude < 0.0001f) return false;
            if (!TryGetBoneSegments(player, bodyPart, out Transform a,
                out Transform b, out Transform c, out Transform d))
                return false;

            Vector3 shotEnd = hitPoint + direction.normalized * MaximumTraceLength;
            float radius = IsArm(bodyPart)
                ? ArteryConfig.ArmBoneRadius.Value
                : ArteryConfig.LegBoneRadius.Value;

            if (a != null && b != null && SegmentCapsuleHit(hitPoint, shotEnd,
                a.position, b.position, radius, out intersection))
                return true;
            if (c != null && d != null && SegmentCapsuleHit(hitPoint, shotEnd,
                c.position, d.position, radius, out intersection))
                return true;
            // Legs have a gap between the knee joint and the calf bone; bridge
            // it so a knee shot still counts as bone.
            return IsLeg(bodyPart) && b != null && c != null && b != c &&
                SegmentCapsuleHit(hitPoint, shotEnd, b.position, c.position,
                    ArteryConfig.LegBoneRadius.Value, out intersection);
        }

        // ---- depth reference -------------------------------------------------

        internal static bool TryGetBodyPartBounds(Player player,
            EBodyPart bodyPart, out Bounds bounds)
        {
            bounds = default;
            BodyPartCollider[] colliders = player?.PlayerBones?.BodyPartColliders;
            if (colliders == null) return false;

            bool hasBounds = false;
            for (int i = 0; i < colliders.Length; i++)
            {
                BodyPartCollider bodyCollider = colliders[i];
                if (bodyCollider == null ||
                    bodyCollider.BodyPartType != bodyPart ||
                    bodyCollider.Collider == null) continue;
                if (!hasBounds)
                {
                    bounds = bodyCollider.Collider.bounds;
                    hasBounds = true;
                }
                else bounds.Encapsulate(bodyCollider.Collider.bounds);
            }
            return hasBounds;
        }

        /// <summary>
        /// Best anatomical centre to measure wound depth against, so a shot is
        /// scored on how close it came to the middle of the limb rather than on
        /// the arbitrary centre of a capsule collider.
        /// </summary>
        internal static bool TryFindDepthReferenceCenter(Player player,
            EBodyPart bodyPart, Vector3 hitPoint, out Vector3 center,
            out string referenceName)
        {
            center = hitPoint;
            referenceName = "collider bounds";

            // The only depth consumer on the head pool is the carotid, which
            // lives in the neck colliders, so measure head-pool wounds against
            // the neck centerline rather than the skull.
            if (bodyPart == EBodyPart.Head &&
                TryGetCervicalSpineSegment(player, out Vector3 skullBase,
                    out Vector3 neckChestTop))
            {
                center = ClosestPointOnSegment(hitPoint, skullBase,
                    neckChestTop);
                referenceName = "neck centerline";
                return true;
            }

            if (bodyPart == EBodyPart.Chest &&
                TryGetBodyPartBounds(player, bodyPart, out Bounds chestBounds))
            {
                center = chestBounds.center;
                referenceName = "chest collider center";
                return true;
            }

            if (bodyPart == EBodyPart.Stomach &&
                TryGetThoracicSpineSegment(player, out Vector3 chestTop,
                    out Vector3 stomachTop))
            {
                center = ClosestPointOnSegment(hitPoint, chestTop, stomachTop);
                referenceName = "torso centerline";
                return true;
            }

            if (!TryGetBoneSegments(player, bodyPart, out Transform firstStart,
                out Transform firstEnd, out Transform secondStart,
                out Transform secondEnd))
                return false;

            float bestDistance = float.MaxValue;
            CaptureNearestSegmentCenter(hitPoint, firstStart, firstEnd,
                ref center, ref bestDistance);
            CaptureNearestSegmentCenter(hitPoint, secondStart, secondEnd,
                ref center, ref bestDistance);
            if (IsLeg(bodyPart) && firstEnd != null && secondStart != null &&
                firstEnd != secondStart)
                CaptureNearestSegmentCenter(hitPoint, firstEnd, secondStart,
                    ref center, ref bestDistance);
            if (bestDistance == float.MaxValue) return false;

            referenceName = IsLeg(bodyPart)
                ? "nearest leg bone center" : "nearest arm bone center";
            return true;
        }

        // ---- maths -----------------------------------------------------------

        internal static bool IsArm(EBodyPart bodyPart) =>
            bodyPart == EBodyPart.LeftArm || bodyPart == EBodyPart.RightArm;

        internal static bool IsLeg(EBodyPart bodyPart) =>
            bodyPart == EBodyPart.LeftLeg || bodyPart == EBodyPart.RightLeg;

        private static void CaptureNearestSegmentCenter(Vector3 hitPoint,
            Transform start, Transform end, ref Vector3 center,
            ref float bestDistance)
        {
            if (start == null || end == null) return;
            Vector3 candidate = ClosestPointOnSegment(hitPoint,
                start.position, end.position);
            float distance = (candidate - hitPoint).sqrMagnitude;
            if (distance >= bestDistance) return;
            bestDistance = distance;
            center = candidate;
        }

        internal static Vector3 ClosestPointOnSegment(Vector3 point,
            Vector3 start, Vector3 end)
        {
            Vector3 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared <= 0.000001f) return start;
            float position = Mathf.Clamp01(
                Vector3.Dot(point - start, segment) / lengthSquared);
            return start + segment * position;
        }

        /// <summary>
        /// Closest approach between the bullet segment and a bone segment. Hit
        /// when the two come within the capsule radius of each other.
        /// </summary>
        internal static bool SegmentCapsuleHit(Vector3 shotStart,
            Vector3 shotEnd, Vector3 boneStart, Vector3 boneEnd, float radius,
            out Vector3 shotClosest)
        {
            Vector3 u = shotEnd - shotStart;
            Vector3 v = boneEnd - boneStart;
            Vector3 w = shotStart - boneStart;
            float a = Vector3.Dot(u, u), b = Vector3.Dot(u, v),
                  c = Vector3.Dot(v, v), d = Vector3.Dot(u, w),
                  e = Vector3.Dot(v, w);
            float denominator = a * c - b * b;
            float s = denominator > 0.000001f
                ? Mathf.Clamp01((b * e - c * d) / denominator) : 0f;
            float t = c > 0.000001f ? Mathf.Clamp01((b * s + e) / c) : 0f;
            s = a > 0.000001f ? Mathf.Clamp01((b * t - d) / a) : 0f;
            Vector3 onShot = shotStart + u * s;
            Vector3 onBone = boneStart + v * t;
            shotClosest = onShot;
            return (onShot - onBone).sqrMagnitude <= radius * radius;
        }

        // ---- bone transform resolution ---------------------------------------

        private static Transform Original(BifacialTransform bone) =>
            bone?.Original;

        private static Transform FindBone(IDictionary<string, Transform> bones,
            params string[] names)
        {
            if (bones == null) return null;
            for (int i = 0; i < names.Length; i++)
                if (bones.TryGetValue(names[i], out Transform found) &&
                    found != null)
                    return found;
            return null;
        }

        /// <summary>
        /// Resolves the bone transforms for one player once, retrying at most
        /// once a second until the rig is fully populated.
        /// </summary>
        internal sealed class LimbBoneCache
        {
            internal Transform LeftShoulder, LeftElbow, LeftHand;
            internal Transform RightShoulder, RightElbow, RightHand;
            internal Transform LeftHip, LeftKnee, LeftCalf, LeftFoot;
            internal Transform RightHip, RightKnee, RightCalf, RightFoot;
            private bool _complete;
            private float _nextRetry;

            internal void Resolve(Player player)
            {
                if (_complete || Time.unscaledTime < _nextRetry ||
                    player == null || player.PlayerBones == null) return;
                _nextRetry = Time.unscaledTime + 1f;

                PlayerBones bones = player.PlayerBones;
                LeftShoulder = LeftShoulder ?? Original(bones.LeftShoulder);
                RightShoulder = RightShoulder ?? Original(bones.RightShoulder);
                LeftHip = LeftHip ?? Original(bones.LeftThigh1);
                RightHip = RightHip ?? Original(bones.RightThigh1);
                LeftKnee = LeftKnee ?? Original(bones.LeftThigh2);
                RightKnee = RightKnee ?? Original(bones.RightThigh2);
                LeftHand = LeftHand ?? bones.LeftPalm;
                RightHand = RightHand ?? bones.RightPalm;

                IDictionary<string, Transform> skeleton =
                    player.PlayerBody?.SkeletonRootJoint?.Bones;
                LeftElbow = LeftElbow ?? FindBone(skeleton,
                    "HumanLForearm1", "HumanLForearm2", "LeftForearm");
                RightElbow = RightElbow ?? FindBone(skeleton,
                    "HumanRForearm1", "HumanRForearm2", "RightForearm");
                LeftCalf = LeftCalf ?? FindBone(skeleton, "HumanLCalf", "LeftCalf");
                RightCalf = RightCalf ?? FindBone(skeleton, "HumanRCalf", "RightCalf");
                LeftFoot = LeftFoot ?? FindBone(skeleton,
                    "HumanLFoot", "LeftFoot", "LFoot");
                RightFoot = RightFoot ?? FindBone(skeleton,
                    "HumanRFoot", "RightFoot", "RFoot");

                Transform[] hierarchy = null;
                if (LeftElbow == null || RightElbow == null || LeftCalf == null ||
                    RightCalf == null || LeftFoot == null || RightFoot == null)
                    hierarchy = player.GetComponentsInChildren<Transform>(true);
                LeftElbow = LeftElbow ?? FindHierarchyBone(hierarchy,
                    "HumanLForearm1", "HumanLForearm2", "LeftForearm");
                RightElbow = RightElbow ?? FindHierarchyBone(hierarchy,
                    "HumanRForearm1", "HumanRForearm2", "RightForearm");
                LeftCalf = LeftCalf ?? FindHierarchyBone(hierarchy,
                    "HumanLCalf", "LeftCalf");
                RightCalf = RightCalf ?? FindHierarchyBone(hierarchy,
                    "HumanRCalf", "RightCalf");
                LeftFoot = LeftFoot ?? FindHierarchyBone(hierarchy,
                    "HumanLFoot", "LeftFoot", "LFoot");
                RightFoot = RightFoot ?? FindHierarchyBone(hierarchy,
                    "HumanRFoot", "RightFoot", "RFoot");

                Animator animator = FindHumanAnimator(player);
                if (animator != null)
                {
                    SetMissing(ref LeftShoulder, animator, HumanBodyBones.LeftUpperArm);
                    SetMissing(ref LeftElbow, animator, HumanBodyBones.LeftLowerArm);
                    SetMissing(ref LeftHand, animator, HumanBodyBones.LeftHand);
                    SetMissing(ref RightShoulder, animator, HumanBodyBones.RightUpperArm);
                    SetMissing(ref RightElbow, animator, HumanBodyBones.RightLowerArm);
                    SetMissing(ref RightHand, animator, HumanBodyBones.RightHand);
                    SetMissing(ref LeftHip, animator, HumanBodyBones.LeftUpperLeg);
                    SetMissing(ref LeftKnee, animator, HumanBodyBones.LeftLowerLeg);
                    SetMissing(ref LeftCalf, animator, HumanBodyBones.LeftLowerLeg);
                    SetMissing(ref LeftFoot, animator, HumanBodyBones.LeftFoot);
                    SetMissing(ref RightHip, animator, HumanBodyBones.RightUpperLeg);
                    SetMissing(ref RightKnee, animator, HumanBodyBones.RightLowerLeg);
                    SetMissing(ref RightCalf, animator, HumanBodyBones.RightLowerLeg);
                    SetMissing(ref RightFoot, animator, HumanBodyBones.RightFoot);
                }

                _complete = LeftShoulder != null && LeftElbow != null &&
                    LeftHand != null && RightShoulder != null &&
                    RightElbow != null && RightHand != null &&
                    LeftHip != null && LeftKnee != null && LeftFoot != null &&
                    RightHip != null && RightKnee != null && RightFoot != null;
            }

            private static Animator FindHumanAnimator(Player player)
            {
                Animator[] animators =
                    player.GetComponentsInChildren<Animator>(true);
                for (int i = 0; i < animators.Length; i++)
                    if (animators[i] != null && animators[i].isHuman)
                        return animators[i];
                return null;
            }

            private static void SetMissing(ref Transform target,
                Animator animator, HumanBodyBones bone)
            {
                if (target == null) target = animator.GetBoneTransform(bone);
            }

            private static Transform FindHierarchyBone(Transform[] hierarchy,
                params string[] names)
            {
                if (hierarchy == null) return null;
                for (int i = 0; i < hierarchy.Length; i++)
                {
                    Transform candidate = hierarchy[i];
                    if (candidate == null) continue;
                    for (int n = 0; n < names.Length; n++)
                        if (candidate.name.EndsWith(names[n],
                            StringComparison.OrdinalIgnoreCase))
                            return candidate;
                }
                return null;
            }
        }
    }
}
