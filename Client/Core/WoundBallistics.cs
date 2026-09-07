using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace ArteryCore
{
    /// <summary>
    /// Turns one bullet impact into a measured wound: how thick the limb is
    /// along the bullet path, how far the bullet actually travelled into it,
    /// and whether it exited. ArteryCore uses this for two things only, the
    /// artery depth gate and the arterial blood-loss scale, plus resolving
    /// whether a bone capsule was within reach.
    /// </summary>
    internal readonly struct WoundBallistics
    {
        private const float ColliderJoinTolerance = 0.02f;
        private const float FullDepthFraction = 0.75f;
        private const float MinimumEntryScale = 0.25f;
        private const float ExitWoundBonus = 0.35f;
        private const float MinimumTissuePenetration = 0.04f;
        private const float PenetrationPowerDepthScale = 0.0055f;
        private const float KineticDepthScale = 0.0032f;
        private const float PelletPositionTolerance = 0.06f;
        private const float PelletDirectionDot = 0.995f;
        private const float BurstCacheDuration = 0.12f;
        private const float BurstPositionTolerance = 0.025f;
        private const float BurstDirectionDot = 0.998f;

        private static readonly List<BodyDepthCacheEntry> BodyDepthCache =
            new List<BodyDepthCacheEntry>(16);
        private static readonly List<DepthReferenceCacheEntry>
            DepthReferenceCache = new List<DepthReferenceCacheEntry>(16);
        private static readonly List<ImpactVelocityCacheEntry>
            ImpactVelocityCache = new List<ImpactVelocityCacheEntry>(8);

        internal readonly Vector3 EntryPoint;
        internal readonly Vector3 ExitPoint;
        internal readonly float TissueThickness;
        internal readonly float PenetrationDepth;
        internal readonly float TraveledDepth;
        internal readonly float ReferenceThickness;
        internal readonly float CenterDepthRatio;
        internal readonly float DepthRatio;
        internal readonly float ImpactVelocity;
        internal readonly float BulletDiameter;
        internal readonly string DepthReferenceName;
        internal readonly string PenetrationModel;
        internal readonly bool PassedThrough;

        /// <summary>
        /// Scales an arterial wound by how deep the bullet went and whether it
        /// left an exit wound. A graze that barely broke the skin bleeds far
        /// less than a through-and-through.
        /// </summary>
        internal float ArterialSeverityScale =>
            Mathf.Lerp(MinimumEntryScale, 1f, DepthRatio) +
            (PassedThrough ? ExitWoundBonus * DepthRatio : 0f);

        private WoundBallistics(Vector3 entryPoint, Vector3 exitPoint,
            float tissueThickness, float penetrationDepth, float impactVelocity,
            float bulletDiameter, bool passedThrough, float referenceThickness,
            string depthReferenceName, string penetrationModel)
        {
            EntryPoint = entryPoint;
            ExitPoint = exitPoint;
            TissueThickness = tissueThickness;
            PenetrationDepth = penetrationDepth;
            ImpactVelocity = impactVelocity;
            BulletDiameter = bulletDiameter;
            PassedThrough = passedThrough;
            ReferenceThickness = referenceThickness;
            DepthReferenceName = depthReferenceName;
            PenetrationModel = penetrationModel;
            TraveledDepth = FindTraveledDepth(penetrationDepth, tissueThickness);

            CenterDepthRatio = referenceThickness > 0.001f
                ? Mathf.Clamp01(TraveledDepth / referenceThickness) : 1f;
            float fullDepth = referenceThickness * FullDepthFraction;
            DepthRatio = fullDepth > 0.001f
                ? Mathf.Clamp01(TraveledDepth / fullDepth) : 1f;
        }

        // =====================================================================

        internal static WoundBallistics Evaluate(Player player,
            EBodyPart bodyPart, DamageInfo damageInfo)
        {
            Vector3 direction = damageInfo.Direction.sqrMagnitude > 0.0001f
                ? damageInfo.Direction.normalized : Vector3.forward;
            Vector3 entry = damageInfo.HitPoint;

            bool hasExit = TryMeasureBodyPart(player, bodyPart,
                damageInfo.HitCollider, entry, direction, out Vector3 exit,
                out float thickness);
            TryMeasureCenterLine(player, bodyPart, damageInfo.HitCollider,
                entry, direction, out _, out _, out float referenceThickness,
                out string depthReferenceName);

            AmmoTemplate ammo = FindAmmoTemplate(damageInfo.SourceId);
            float diameter = ammo != null
                ? Mathf.Max(1f, ammo.BulletDiameterMilimeters) : 7f;
            float initialVelocity = ammo != null
                ? Mathf.Max(1f, ammo.InitialSpeed) : 700f;
            float impactVelocity = ammo != null
                ? FindImpactVelocity(damageInfo, ammo, direction)
                : initialVelocity;
            float velocityRatio = Mathf.Clamp(impactVelocity / initialVelocity,
                0.25f, 1.25f);

            // EFT penetration power is an abstract armor value. Convert it into
            // a tunable tissue depth so geometry can actually be tested.
            float diameterResistance = Mathf.Clamp(7f / diameter, 0.65f, 1.25f);
            float armorPenetrationDepth = (MinimumTissuePenetration +
                Mathf.Max(0f, damageInfo.PenetrationPower) *
                    PenetrationPowerDepthScale) *
                velocityRatio * diameterResistance;

            float projectileMassKg = ammo != null
                ? Mathf.Max(0.0001f, ammo.BulletMassGram * 0.001f) : 0.008f;
            float impactEnergyJoules = 0.5f * projectileMassKg *
                impactVelocity * impactVelocity;
            float kineticPenetrationDepth = MinimumTissuePenetration +
                Mathf.Sqrt(Mathf.Max(0f, impactEnergyJoules)) * KineticDepthScale;

            bool usesKineticFloor =
                kineticPenetrationDepth > armorPenetrationDepth;
            float penetrationDepth = usesKineticFloor
                ? kineticPenetrationDepth : armorPenetrationDepth;
            bool passedThrough = hasExit && penetrationDepth >= thickness;

            Vector3 displayedEnd = passedThrough && hasExit
                ? exit : entry + direction * penetrationDepth;
            return new WoundBallistics(entry, displayedEnd, thickness,
                penetrationDepth, impactVelocity, diameter, passedThrough,
                referenceThickness, depthReferenceName,
                usesKineticFloor ? "kinetic tissue floor" : "armor penetration");
        }

        /// <summary>
        /// Bone eats penetration depth and velocity. Applied once per bone
        /// capsule the path crosses, nearest first.
        /// </summary>
        internal WoundBallistics ApplyBoneResistance(Vector3 direction,
            float boneDistance, float resistanceDepth, float velocityRetention)
        {
            if (boneDistance < 0f || boneDistance > PenetrationDepth)
                return this;

            float penetrationDepth = boneDistance + Mathf.Max(0f,
                PenetrationDepth - boneDistance -
                Mathf.Max(0f, resistanceDepth));
            float retainedVelocity = ImpactVelocity *
                Mathf.Clamp01(velocityRetention);
            bool passedThrough = TissueThickness > 0.001f &&
                penetrationDepth >= TissueThickness;
            Vector3 normalized = direction.sqrMagnitude > 0.0001f
                ? direction.normalized : Vector3.forward;
            Vector3 displayedEnd = passedThrough
                ? EntryPoint + normalized * TissueThickness
                : EntryPoint + normalized * penetrationDepth;

            return new WoundBallistics(EntryPoint, displayedEnd,
                TissueThickness, penetrationDepth, retainedVelocity,
                BulletDiameter, passedThrough, ReferenceThickness,
                DepthReferenceName, PenetrationModel);
        }

        /// <summary>
        /// Whether the bullet still had the depth budget to reach a point this
        /// far along its path.
        /// </summary>
        internal bool CanReach(float distance) =>
            distance <= PenetrationDepth + 0.002f;

        private static float FindTraveledDepth(float penetrationDepth,
            float tissueThickness) => tissueThickness > 0.001f
                ? Mathf.Min(penetrationDepth, tissueThickness)
                : penetrationDepth;

        // ---- body-part thickness along the bullet path -----------------------

        private static bool TryMeasureBodyPart(Player player,
            EBodyPart bodyPart, Collider hitCollider, Vector3 entry,
            Vector3 direction, out Vector3 exit, out float thickness)
        {
            exit = entry;
            thickness = 0f;
            float now = Time.unscaledTime;
            int frame = Time.frameCount;
            int playerId = player != null ? player.GetInstanceID() : 0;
            int colliderId = hitCollider != null
                ? hitCollider.GetInstanceID() : 0;
            Transform attachment = hitCollider != null
                ? hitCollider.transform
                : player != null ? player.Transform.Original : null;
            Vector3 localEntry = attachment != null
                ? attachment.InverseTransformPoint(entry) : entry;
            Vector3 localDirection = attachment != null
                ? attachment.InverseTransformDirection(direction).normalized
                : direction;

            for (int i = BodyDepthCache.Count - 1; i >= 0; i--)
            {
                BodyDepthCacheEntry cached = BodyDepthCache[i];
                if (now - cached.CapturedAt > BurstCacheDuration ||
                    cached.Attachment == null)
                {
                    BodyDepthCache.RemoveAt(i);
                    continue;
                }
                bool isPellet = cached.CapturedFrame == frame;
                float positionTolerance = isPellet
                    ? PelletPositionTolerance : BurstPositionTolerance;
                float directionDot = isPellet
                    ? PelletDirectionDot : BurstDirectionDot;
                if (cached.PlayerId != playerId ||
                    cached.ColliderId != colliderId ||
                    cached.BodyPart != bodyPart ||
                    cached.Attachment != attachment ||
                    (cached.LocalHitPoint - localEntry).sqrMagnitude >
                        positionTolerance * positionTolerance ||
                    Vector3.Dot(cached.LocalDirection, localDirection) <
                        directionDot) continue;
                thickness = cached.Thickness;
                exit = entry + direction * thickness;
                return thickness > 0.001f;
            }

            bool isResolved = MeasureBodyPart(player, bodyPart, hitCollider,
                entry, direction, out exit, out thickness);
            if (BodyDepthCache.Count >= 32) BodyDepthCache.RemoveAt(0);
            BodyDepthCache.Add(new BodyDepthCacheEntry
            {
                PlayerId = playerId,
                ColliderId = colliderId,
                BodyPart = bodyPart,
                Attachment = attachment,
                LocalHitPoint = localEntry,
                LocalDirection = localDirection,
                Thickness = thickness,
                CapturedAt = now,
                CapturedFrame = frame
            });
            return isResolved;
        }

        private static bool MeasureBodyPart(Player player, EBodyPart bodyPart,
            Collider hitCollider, Vector3 entry, Vector3 direction,
            out Vector3 exit, out float thickness)
        {
            exit = entry;
            thickness = 0f;
            BodyPartCollider[] bodyColliders =
                player?.PlayerBones?.BodyPartColliders;
            if (bodyColliders == null || bodyColliders.Length == 0)
                return TryFindColliderExit(hitCollider, entry, direction,
                    out exit, out thickness);

            List<TissueInterval> tissueIntervals =
                new List<TissueInterval>(bodyColliders.Length);
            for (int i = 0; i < bodyColliders.Length; i++)
            {
                BodyPartCollider bodyCollider = bodyColliders[i];
                if (bodyCollider == null ||
                    bodyCollider.BodyPartType != bodyPart) continue;
                if (TryFindColliderInterval(bodyCollider.Collider, entry,
                    direction, out TissueInterval interval))
                    tissueIntervals.Add(interval);
            }

            if (tissueIntervals.Count == 0)
                return TryFindColliderExit(hitCollider, entry, direction,
                    out exit, out thickness);

            tissueIntervals.Sort((left, right) =>
                left.Start.CompareTo(right.Start));

            int entryIntervalIndex = -1;
            for (int i = 0; i < tissueIntervals.Count; i++)
            {
                TissueInterval interval = tissueIntervals[i];
                if (interval.Start <= ColliderJoinTolerance &&
                    interval.End >= -ColliderJoinTolerance)
                {
                    entryIntervalIndex = i;
                    break;
                }
            }
            if (entryIntervalIndex < 0)
                return TryFindColliderExit(hitCollider, entry, direction,
                    out exit, out thickness);

            float connectedEnd = Mathf.Max(0f,
                tissueIntervals[entryIntervalIndex].End);
            for (int i = entryIntervalIndex + 1; i < tissueIntervals.Count; i++)
            {
                TissueInterval interval = tissueIntervals[i];
                if (interval.Start > connectedEnd + ColliderJoinTolerance) break;
                connectedEnd = Mathf.Max(connectedEnd, interval.End);
            }

            thickness = connectedEnd;
            exit = entry + direction * thickness;
            return thickness > 0.001f;
        }

        // ---- centre-line reference thickness ---------------------------------

        private static bool TryMeasureCenterLine(Player player,
            EBodyPart bodyPart, Collider hitCollider, Vector3 hitPoint,
            Vector3 direction, out Vector3 referenceEntry,
            out Vector3 referenceExit, out float referenceThickness,
            out string referenceName)
        {
            int frame = Time.frameCount;
            float now = Time.unscaledTime;
            int playerId = player != null ? player.GetInstanceID() : 0;
            int colliderId = hitCollider != null
                ? hitCollider.GetInstanceID() : 0;
            Transform attachment = hitCollider != null
                ? hitCollider.transform
                : player != null ? player.Transform.Original : null;
            Vector3 localHitPoint = attachment != null
                ? attachment.InverseTransformPoint(hitPoint) : hitPoint;
            Vector3 localDirection = attachment != null
                ? attachment.InverseTransformDirection(direction).normalized
                : direction;

            for (int i = DepthReferenceCache.Count - 1; i >= 0; i--)
            {
                DepthReferenceCacheEntry cached = DepthReferenceCache[i];
                if (now - cached.CapturedAt > BurstCacheDuration ||
                    cached.Attachment == null)
                {
                    DepthReferenceCache.RemoveAt(i);
                    continue;
                }
                bool isPellet = cached.CapturedFrame == frame;
                float positionTolerance = isPellet
                    ? PelletPositionTolerance : BurstPositionTolerance;
                float directionDot = isPellet
                    ? PelletDirectionDot : BurstDirectionDot;
                if (cached.PlayerId != playerId ||
                    cached.ColliderId != colliderId ||
                    cached.BodyPart != bodyPart ||
                    cached.Attachment != attachment ||
                    (cached.LocalHitPoint - localHitPoint).sqrMagnitude >
                        positionTolerance * positionTolerance ||
                    Vector3.Dot(cached.LocalDirection, localDirection) <
                        directionDot) continue;
                referenceEntry = attachment.TransformPoint(cached.LocalEntry);
                referenceExit = attachment.TransformPoint(cached.LocalExit);
                referenceThickness = cached.Thickness;
                referenceName = cached.Name;
                return cached.IsResolved;
            }

            bool isResolved = MeasureCenterLine(player, bodyPart, hitCollider,
                hitPoint, direction, out referenceEntry, out referenceExit,
                out referenceThickness, out referenceName);
            if (DepthReferenceCache.Count >= 32)
                DepthReferenceCache.RemoveAt(0);
            DepthReferenceCache.Add(new DepthReferenceCacheEntry
            {
                PlayerId = playerId,
                ColliderId = colliderId,
                BodyPart = bodyPart,
                Attachment = attachment,
                LocalHitPoint = localHitPoint,
                LocalDirection = localDirection,
                LocalEntry = attachment != null
                    ? attachment.InverseTransformPoint(referenceEntry)
                    : referenceEntry,
                LocalExit = attachment != null
                    ? attachment.InverseTransformPoint(referenceExit)
                    : referenceExit,
                Thickness = referenceThickness,
                Name = referenceName,
                IsResolved = isResolved,
                CapturedAt = now,
                CapturedFrame = frame
            });
            return isResolved;
        }

        private static bool MeasureCenterLine(Player player, EBodyPart bodyPart,
            Collider hitCollider, Vector3 hitPoint, Vector3 direction,
            out Vector3 referenceEntry, out Vector3 referenceExit,
            out float referenceThickness, out string referenceName)
        {
            referenceEntry = referenceExit = hitCollider != null
                ? hitCollider.bounds.center : Vector3.zero;
            referenceThickness = 0f;
            referenceName = "collider bounds";

            BodyPartCollider[] bodyColliders =
                player?.PlayerBones?.BodyPartColliders;
            Bounds bodyBounds = default;
            bool hasBounds = false;
            if (bodyColliders != null)
                for (int i = 0; i < bodyColliders.Length; i++)
                {
                    BodyPartCollider bodyCollider = bodyColliders[i];
                    if (bodyCollider == null ||
                        bodyCollider.BodyPartType != bodyPart ||
                        bodyCollider.Collider == null) continue;
                    if (!hasBounds)
                    {
                        bodyBounds = bodyCollider.Collider.bounds;
                        hasBounds = true;
                    }
                    else bodyBounds.Encapsulate(bodyCollider.Collider.bounds);
                }

            Vector3 fallbackCenter = hasBounds
                ? bodyBounds.center : referenceEntry;
            bool hasFallback = TryMeasureLineAtCenter(bodyColliders, bodyPart,
                hitCollider, fallbackCenter, direction,
                out Vector3 fallbackEntry, out Vector3 fallbackExit,
                out float fallbackThickness);

            bool hasAnatomicalCenter = BoneGeometry.TryFindDepthReferenceCenter(
                player, bodyPart, hitPoint, out Vector3 anatomicalCenter,
                out string anatomicalName);
            Vector3 anatomicalEntry = anatomicalCenter;
            Vector3 anatomicalExit = anatomicalCenter;
            float anatomicalThickness = 0f;
            bool hasAnatomical = hasAnatomicalCenter && TryMeasureLineAtCenter(
                bodyColliders, bodyPart, hitCollider, anatomicalCenter,
                direction, out anatomicalEntry, out anatomicalExit,
                out anatomicalThickness);

            if (hasAnatomical &&
                (!hasFallback || anatomicalThickness >= fallbackThickness))
            {
                referenceEntry = anatomicalEntry;
                referenceExit = anatomicalExit;
                referenceThickness = anatomicalThickness;
                referenceName = anatomicalName;
                return true;
            }
            if (!hasFallback) return false;

            referenceEntry = fallbackEntry;
            referenceExit = fallbackExit;
            referenceThickness = fallbackThickness;
            referenceName = hasAnatomical
                ? anatomicalName + " + bounds safeguard" : "collider bounds";
            return true;
        }

        private static bool TryMeasureLineAtCenter(
            BodyPartCollider[] bodyColliders, EBodyPart bodyPart,
            Collider hitCollider, Vector3 center, Vector3 direction,
            out Vector3 referenceEntry, out Vector3 referenceExit,
            out float referenceThickness)
        {
            referenceEntry = referenceExit = center;
            referenceThickness = 0f;
            List<TissueInterval> intervals = new List<TissueInterval>();
            if (bodyColliders != null)
                for (int i = 0; i < bodyColliders.Length; i++)
                {
                    BodyPartCollider bodyCollider = bodyColliders[i];
                    if (bodyCollider == null ||
                        bodyCollider.BodyPartType != bodyPart) continue;
                    if (TryFindColliderInterval(bodyCollider.Collider, center,
                        direction, out TissueInterval interval))
                        intervals.Add(interval);
                }
            if (intervals.Count == 0 && TryFindColliderInterval(hitCollider,
                center, direction, out TissueInterval fallback))
                intervals.Add(fallback);
            if (!TryFindLongestConnectedInterval(intervals, out float start,
                out float end)) return false;

            referenceEntry = center + direction * start;
            referenceExit = center + direction * end;
            referenceThickness = end - start;
            return referenceThickness > 0.001f;
        }

        private static bool TryFindLongestConnectedInterval(
            List<TissueInterval> intervals, out float longestStart,
            out float longestEnd)
        {
            longestStart = longestEnd = 0f;
            if (intervals == null || intervals.Count == 0) return false;
            intervals.Sort((left, right) => left.Start.CompareTo(right.Start));

            float groupStart = intervals[0].Start;
            float groupEnd = intervals[0].End;
            for (int i = 1; i <= intervals.Count; i++)
            {
                if (i < intervals.Count &&
                    intervals[i].Start <= groupEnd + ColliderJoinTolerance)
                {
                    groupEnd = Mathf.Max(groupEnd, intervals[i].End);
                    continue;
                }
                if (groupEnd - groupStart > longestEnd - longestStart)
                {
                    longestStart = groupStart;
                    longestEnd = groupEnd;
                }
                if (i < intervals.Count)
                {
                    groupStart = intervals[i].Start;
                    groupEnd = intervals[i].End;
                }
            }
            return longestEnd - longestStart > 0.001f;
        }

        private static bool TryFindColliderInterval(Collider collider,
            Vector3 entry, Vector3 direction, out TissueInterval interval)
        {
            interval = default;
            if (collider == null || !collider.enabled) return false;

            float searchDistance = Vector3.Distance(entry,
                collider.bounds.center) + collider.bounds.size.magnitude + 0.25f;
            Vector3 beforeCollider = entry - direction * searchDistance;
            Vector3 afterCollider = entry + direction * searchDistance;
            if (!collider.Raycast(new Ray(beforeCollider, direction),
                out RaycastHit entryHit, searchDistance * 2f)) return false;
            if (!collider.Raycast(new Ray(afterCollider, -direction),
                out RaycastHit exitHit, searchDistance * 2f)) return false;

            float start = Vector3.Dot(entryHit.point - entry, direction);
            float end = Vector3.Dot(exitHit.point - entry, direction);
            if (end < start)
            {
                float previousStart = start;
                start = end;
                end = previousStart;
            }
            interval = new TissueInterval(start, end);
            return end - start > 0.001f;
        }

        private static bool TryFindColliderExit(Collider collider, Vector3 entry,
            Vector3 direction, out Vector3 exit, out float thickness)
        {
            exit = entry;
            thickness = 0f;
            if (!TryFindColliderInterval(collider, entry, direction,
                out TissueInterval interval)) return false;
            thickness = Mathf.Max(0f, interval.End);
            exit = entry + direction * thickness;
            return thickness > 0.001f;
        }

        // ---- ammunition ------------------------------------------------------

        private static AmmoTemplate FindAmmoTemplate(string templateId)
        {
            if (string.IsNullOrEmpty(templateId) ||
                !Singleton<ItemFactory>.Instantiated) return null;
            return Singleton<ItemFactory>.Instance.ItemTemplates.TryGetValue(
                templateId, out ItemTemplate template)
                ? template as AmmoTemplate : null;
        }

        private static float FindImpactVelocity(DamageInfo damageInfo,
            AmmoTemplate ammo, Vector3 direction)
        {
            float now = Time.unscaledTime;
            int frame = Time.frameCount;
            for (int i = ImpactVelocityCache.Count - 1; i >= 0; i--)
            {
                ImpactVelocityCacheEntry cached = ImpactVelocityCache[i];
                if (now - cached.CapturedAt > BurstCacheDuration)
                {
                    ImpactVelocityCache.RemoveAt(i);
                    continue;
                }
                bool isPellet = cached.CapturedFrame == frame;
                float hitTolerance = isPellet
                    ? PelletPositionTolerance : BurstPositionTolerance;
                float directionDot = isPellet
                    ? PelletDirectionDot : BurstDirectionDot;
                if (cached.AmmoId != damageInfo.SourceId ||
                    (cached.Origin - damageInfo.MasterOrigin).sqrMagnitude >
                        0.01f ||
                    (cached.HitPoint - damageInfo.HitPoint).sqrMagnitude >
                        hitTolerance * hitTolerance ||
                    Vector3.Dot(cached.Direction, direction) < directionDot)
                    continue;
                return cached.Velocity;
            }

            float velocity = EstimateImpactVelocity(damageInfo, ammo, direction);
            if (ImpactVelocityCache.Count >= 16) ImpactVelocityCache.RemoveAt(0);
            ImpactVelocityCache.Add(new ImpactVelocityCacheEntry
            {
                AmmoId = damageInfo.SourceId,
                Origin = damageInfo.MasterOrigin,
                HitPoint = damageInfo.HitPoint,
                Direction = direction,
                Velocity = velocity,
                CapturedAt = now,
                CapturedFrame = frame
            });
            return velocity;
        }

        private static float EstimateImpactVelocity(DamageInfo damageInfo,
            AmmoTemplate ammo, Vector3 direction)
        {
            float initialVelocity = Mathf.Max(1f, ammo.InitialSpeed);
            if (!Singleton<GameWorld>.Instantiated) return initialVelocity;

            TrajectoryCalculator trajectory = null;
            try
            {
                trajectory = new TrajectoryCalculator();
                trajectory.Initialize(damageInfo.MasterOrigin,
                    direction * initialVelocity, ammo.BulletMassGram,
                    ammo.BulletDiameterMilimeters, ammo.BallisticCoeficient);
                float bestDistance =
                    (damageInfo.MasterOrigin - damageInfo.HitPoint).sqrMagnitude;
                float bestVelocity = initialVelocity;
                for (int i = 0; i < trajectory.MaxAllowedLength - 1; i++)
                {
                    TrajectoryInfo point = trajectory.Next();
                    float distance =
                        (point.position - damageInfo.HitPoint).sqrMagnitude;
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        bestVelocity = point.velocity.magnitude;
                        continue;
                    }
                    if (i > 2) break;
                }
                return bestVelocity;
            }
            catch
            {
                return initialVelocity;
            }
            finally
            {
                if (trajectory != null && trajectory.history != null)
                    trajectory.ClearClass();
            }
        }

        internal static void ClearCaches()
        {
            BodyDepthCache.Clear();
            DepthReferenceCache.Clear();
            ImpactVelocityCache.Clear();
        }

        // ---- cache records ---------------------------------------------------

        private readonly struct TissueInterval
        {
            internal readonly float Start;
            internal readonly float End;

            internal TissueInterval(float start, float end)
            {
                Start = start;
                End = end;
            }
        }

        private sealed class BodyDepthCacheEntry
        {
            internal int PlayerId;
            internal int ColliderId;
            internal EBodyPart BodyPart;
            internal Transform Attachment;
            internal Vector3 LocalHitPoint;
            internal Vector3 LocalDirection;
            internal float Thickness;
            internal float CapturedAt;
            internal int CapturedFrame;
        }

        private sealed class DepthReferenceCacheEntry
        {
            internal int PlayerId;
            internal int ColliderId;
            internal EBodyPart BodyPart;
            internal Transform Attachment;
            internal Vector3 LocalHitPoint;
            internal Vector3 LocalDirection;
            internal Vector3 LocalEntry;
            internal Vector3 LocalExit;
            internal float Thickness;
            internal string Name;
            internal bool IsResolved;
            internal float CapturedAt;
            internal int CapturedFrame;
        }

        private sealed class ImpactVelocityCacheEntry
        {
            internal string AmmoId;
            internal Vector3 Origin;
            internal Vector3 HitPoint;
            internal Vector3 Direction;
            internal float Velocity;
            internal float CapturedAt;
            internal int CapturedFrame;
        }
    }
}
