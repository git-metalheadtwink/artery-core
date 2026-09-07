using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using ArteryCore.Patches.Effects;
using ArteryCore.Patches.Presentation;
using Systems.Effects;
using UnityEngine;

namespace ArteryCore
{
    /// <summary>
    /// Per-player arterial wound state. Attached on demand to any player who
    /// takes a qualifying hit, and disables itself once it has nothing left to
    /// simulate.
    /// </summary>
    internal sealed class ArteryController : MonoBehaviour
    {
        private const float TickStep = 1f / 60f;
        private const float MaximumBleedPresentationInterval = 1f;
        private const float MinimumBleedPresentationInterval = 0.5f;
        private const int MaximumPresentationBleedCount = 7;
        private const float CorpseBleedDuration = 8f;
        private const int MaximumBloodSources = 24;
        private const int MaximumBloodParticles = 192;
        private const float BloodLossStimBleedMultiplier = 0.50f;

        // ---- arterial wounds -------------------------------------------------

        private sealed class ArterialWound
        {
            internal float Severity;
            internal float Created;
            internal float Duration;
            internal float FrontLoad;
            internal ArteryZoneKind Zone;
        }

        private sealed class ArteryTrack
        {
            private readonly List<ArterialWound> _wounds =
                new List<ArterialWound>();

            internal int Count => _wounds.Count;
            internal bool Active => _wounds.Count > 0;

            /// <summary>
            /// Whoever opened the most recent wound on this part, so a
            /// bleed-out is still credited to them.
            /// </summary>
            internal IObserverToPlayerBridge Aggressor;

            internal float Add(ArteryZoneKind zone, float totalBloodLoss,
                in ArteryProfile profile)
            {
                float severity = Mathf.Max(0f, totalBloodLoss) /
                    profile.GetDecayArea();

                while (_wounds.Count >= profile.StackCap)
                    _wounds.RemoveAt(0);

                _wounds.Add(new ArterialWound
                {
                    Severity = severity,
                    Created = Time.unscaledTime,
                    Duration = profile.BleedDuration,
                    FrontLoad = profile.FrontLoad,
                    Zone = zone
                });
                return severity;
            }

            /// <summary>
            /// Unescalated bleed rate. Each wound keeps the duration and curve
            /// shape it was opened with, so retuning mid-raid cannot corrupt
            /// wounds that are already bleeding.
            /// </summary>
            internal float GetDamagePerSecond()
            {
                float now = Time.unscaledTime;
                float damagePerSecond = 0f;
                for (int i = 0; i < _wounds.Count; i++)
                {
                    ArterialWound wound = _wounds[i];
                    damagePerSecond += wound.Severity *
                        ArteryConfig.GetBleedStrength(now - wound.Created,
                            wound.Duration, wound.FrontLoad);
                }
                return damagePerSecond;
            }

            internal float GetTimeLeft()
            {
                float now = Time.unscaledTime;
                float timeLeft = 0f;
                for (int i = 0; i < _wounds.Count; i++)
                {
                    ArterialWound wound = _wounds[i];
                    timeLeft = Mathf.Max(timeLeft,
                        wound.Duration - (now - wound.Created));
                }
                return Mathf.Max(0f, timeLeft);
            }

            internal ArteryZoneKind GetDominantZone()
            {
                ArteryZoneKind dominant = ArteryZoneKind.None;
                float strongest = -1f;
                for (int i = 0; i < _wounds.Count; i++)
                    if (_wounds[i].Severity > strongest)
                    {
                        strongest = _wounds[i].Severity;
                        dominant = _wounds[i].Zone;
                    }
                return dominant;
            }

            /// <summary>
            /// Pushes every wound forward along its clot timer. Progress 1 ends
            /// the bleed outright, which is what a vanilla bandage does.
            /// </summary>
            internal void AdvanceClotting(float progress)
            {
                if (!Active) return;
                progress = Mathf.Clamp01(progress);
                if (progress >= 1f)
                {
                    _wounds.Clear();
                    return;
                }
                float now = Time.unscaledTime;
                float remainingMultiplier = 1f - progress;
                for (int i = 0; i < _wounds.Count; i++)
                {
                    ArterialWound wound = _wounds[i];
                    float elapsed = Mathf.Clamp(now - wound.Created, 0f,
                        wound.Duration);
                    float remaining = (wound.Duration - elapsed) *
                        remainingMultiplier;
                    wound.Created = now - (wound.Duration - remaining);
                }
            }

            internal void RemoveClottedWounds()
            {
                float now = Time.unscaledTime;
                for (int i = _wounds.Count - 1; i >= 0; i--)
                    if (now - _wounds[i].Created >= _wounds[i].Duration)
                        _wounds.RemoveAt(i);
            }

            internal void Clear() { _wounds.Clear(); }
        }

        // ---- blood -----------------------------------------------------------

        internal struct BloodParticle
        {
            internal Vector3 Position;
            internal Vector3 Velocity;
            internal float Expires;
            internal float Size;
            internal float TrailLength;
        }

        private sealed class BloodSource
        {
            internal Transform Attachment;
            internal Vector3 LocalPosition;
            internal Vector3 LocalDirection;
            internal float Strength;
            internal float Created;
            internal float Duration;
            internal float FrontLoad;
            internal float EmissionAccumulator;
            internal EBodyPart BodyPart;
        }

        // ---- state -----------------------------------------------------------

        private static readonly EBodyPart[] ArterialBodyParts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm,
            EBodyPart.LeftLeg, EBodyPart.RightLeg
        };

        private static readonly EBodyPart[] HeadSharedTargets =
            { EBodyPart.Chest };
        private static readonly EBodyPart[] ChestSharedTargets =
            { EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.Stomach };
        private static readonly EBodyPart[] StomachSharedTargets =
            { EBodyPart.Chest, EBodyPart.LeftLeg, EBodyPart.RightLeg };
        private static readonly EBodyPart[] ArmSharedTargets =
            { EBodyPart.Chest };
        private static readonly EBodyPart[] LegSharedTargets =
            { EBodyPart.Stomach };

        private Player _player;
        private ActiveHealthController _health;
        private bool _subscribed;
        private bool _addingMarker;
        private float _tickAccumulator;
        private float _nextPresentationTime;
        private float _nextBloodDecalTime;
        private float _requestedDamageTotal;
        private float _acceptedDamageTotal;
        private float _nextDiagnosticTime;
        private readonly Dictionary<EBodyPart, float> _acceptedByPart =
            new Dictionary<EBodyPart, float>();
        private bool _wasAlive;
        private bool _corpseBloodInitialized;
        private float _corpseBloodReserve;
        private bool _bleedDeathVoicePending;

        private float _bruiseStrength;
        private float _currentBruiseStrength;
        private float _bruiseExpires;
        private float _appliedRestorePenalty;
        private ActiveHealthController.Pain _bruiseUiEffect;

        private Vector3 _lastImpactPoint;
        private Vector3 _lastImpactDirection = Vector3.forward;
        private Transform _lastImpactTransform;

        private readonly Dictionary<EBodyPart, ArteryTrack> _tracks =
            new Dictionary<EBodyPart, ArteryTrack>();
        private readonly List<BloodSource> _bloodSources =
            new List<BloodSource>(16);
        private readonly List<BloodParticle> _bloodParticles =
            new List<BloodParticle>(MaximumBloodParticles);
        private readonly List<BodyRenderer> _bodyRenderers =
            new List<BodyRenderer>(12);
        private readonly HashSet<IHealthEffect> _queuedTreatmentEffects =
            new HashSet<IHealthEffect>();
        private readonly List<IHealthEffect> _pendingTreatments =
            new List<IHealthEffect>();

        internal float BruiseStrength => _currentBruiseStrength;
        internal bool BleedDeathVoicePending => _bleedDeathVoicePending;
        internal IList<BloodParticle> BloodParticles => _bloodParticles;

        internal int ArterialWoundCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<EBodyPart, ArteryTrack> pair in _tracks)
                    count += pair.Value.Count;
                return count;
            }
        }

        internal float TotalBleedDamagePerSecond
        {
            get
            {
                float total = 0f;
                foreach (KeyValuePair<EBodyPart, ArteryTrack> pair in _tracks)
                    total += pair.Value.GetDamagePerSecond();
                return total * GetBleedRateMultiplier() *
                    ArteryProfile.Resolve(_player)
                        .GetEscalation(ArterialWoundCount);
            }
        }

        // =====================================================================

        internal static ArteryController GetOrCreate(Player player)
        {
            ArteryController controller = player.GetComponent<ArteryController>();
            if (controller == null)
                controller = player.gameObject.AddComponent<ArteryController>();
            controller.InitializeForPlayer(player);
            return controller;
        }

        internal void InitializeForPlayer(Player player)
        {
            ActiveHealthController nextHealth = player?.ActiveHealthController;
            if (_player == player && _health == nextHealth) return;

            Unsubscribe();
            _player = player;
            _health = nextHealth;
            if (_health != null)
            {
                _wasAlive = _health.IsAlive;
                _corpseBloodInitialized = false;
                _corpseBloodReserve = 0f;
                _health.EffectRemovedEvent += OnEffectRemoved;
                _health.EffectResidualEvent += OnEffectResidual;
                _subscribed = true;
            }
            enabled = HasRecurringWork();
        }

        // ---- impact intake ---------------------------------------------------

        /// <summary>
        /// Records where the last bullet landed so blood spray originates from
        /// the wound rather than the player origin.
        /// </summary>
        internal void CaptureImpact(Vector3 hitPoint, Vector3 direction,
            Transform hitTransform)
        {
            _lastImpactPoint = hitPoint;
            _lastImpactDirection = direction.sqrMagnitude > 0.0001f
                ? direction.normalized : Vector3.forward;
            _lastImpactTransform = hitTransform != null
                ? hitTransform
                : _player != null ? _player.gameObject.transform : null;
        }

        /// <summary>
        /// Opens an arterial wound. The blood-loss budget is derived from the
        /// post-armor damage of the bullet; the bullet damage itself is never
        /// touched, so this is always additional to vanilla.
        /// </summary>
        internal void AddArterialWound(EBodyPart bodyPart, ArteryZoneKind zone,
            float postArmorDamage, float severityScale, bool passedThrough,
            IObserverToPlayerBridge aggressor)
        {
            if (_health == null || !_health.IsAlive) return;

            ArteryProfile profile = ArteryProfile.Resolve(_player);
            float budget = Mathf.Max(0f, postArmorDamage) *
                profile.BleedBudget *
                ArteryConfig.GetZoneSeverity(zone) *
                Mathf.Max(0f, severityScale) *
                profile.DamageMultiplier;
            if (budget <= 0f) return;

            // Each budget unit is split between the systemic route and the
            // local linkage route, and linkage pays out extra to neighbouring
            // parts. Divide that back out so the configured total blood loss
            // is what actually lands, whatever the routing is set to.
            budget /= profile.GetDeliveryMultiplier(
                EstimateLinkedDamageMultiplier(bodyPart));

            if (!_tracks.TryGetValue(bodyPart, out ArteryTrack track))
            {
                track = new ArteryTrack();
                _tracks.Add(bodyPart, track);
            }
            if (aggressor != null) track.Aggressor = aggressor;

            float severity = track.Add(zone, budget, profile);
            enabled = true;

            AddBloodSource(severity, bodyPart, profile.BleedDuration,
                profile.FrontLoad, passedThrough);
            EnsureMarkers();

            int wounds = ArterialWoundCount;
            ArteryLog.Info(string.Format(
                "[Artery] {0} severed on {1} ({2}): budget={3:0.0} HP over " +
                "{4:0.0}s (start {5:0.00} HP/s), wounds={6}, escalation={7:0.00}x",
                ArteryZones.GetDisplayName(zone), bodyPart, profile.Name,
                budget, profile.BleedDuration, severity, wounds,
                profile.GetEscalation(wounds)));
        }

        internal void AddBruise(float stoppedBulletDamage)
        {
            if (!ArteryConfig.EnableBruising.Value) return;
            enabled = true;

            float duration = ArteryConfig.BruiseDuration.Value;
            _bruiseStrength = Mathf.Clamp01(_currentBruiseStrength +
                Mathf.Max(0f, stoppedBulletDamage) / 100f);
            _bruiseExpires = Time.unscaledTime + duration;
            _currentBruiseStrength = _bruiseStrength;

            if (_health != null)
            {
                _bruiseUiEffect = _health.FindExistingEffect<
                    ActiveHealthController.Pain>(EBodyPart.Chest);
                if (_bruiseUiEffect == null)
                    _bruiseUiEffect = _health.AddEffect<
                        ActiveHealthController.Pain>(EBodyPart.Chest, 0f,
                            duration, 0f, _bruiseStrength);
                else
                {
                    _bruiseUiEffect.AddWorkTime(duration, true);
                    if (_bruiseStrength > _bruiseUiEffect.Strength)
                        _bruiseUiEffect.SetStrength(_bruiseStrength);
                }
                NativeEffectLabels.MarkBruised(_bruiseUiEffect, duration);
            }

            ArteryLog.Info(string.Format(
                "[Bruise] +{0:0.00} strength={1:0.00} for {2:0.0}s",
                stoppedBulletDamage / 100f, _bruiseStrength, duration));
        }

        /// <summary>
        /// Stains the body mesh at the impact point using the native decal
        /// painter, so the wound is visible on the model.
        /// </summary>
        internal void PaintBloodAtHit(Vector3 hitPoint, Vector3 hitNormal)
        {
            if (_player == null || _player.PlayerBody == null ||
                !Singleton<Effects>.Instantiated) return;
            Effects effects = Singleton<Effects>.Instance;
            if (effects == null || !effects.UseDecalPainter ||
                effects.TexDecals == null) return;

            _bodyRenderers.Clear();
            _player.PlayerBody.GetBodyRenderersNonAlloc(_bodyRenderers);
            if (_bodyRenderers.Count == 0) return;

            Vector3 projectionNormal = hitNormal.sqrMagnitude > 0.0001f
                ? -hitNormal.normalized : Vector3.back;
            effects.PlayerMeshesHit(_bodyRenderers, hitPoint, projectionNormal);
        }

        /// <summary>
        /// Adds a short, finite blood source when a corpse is shot.
        /// </summary>
        internal void AddCorpseWound(EBodyPart bodyPart)
        {
            if (_health == null || !ArteryConfig.EnableBloodEffects.Value) return;
            EnsureCorpseBloodReserve();
            if (_corpseBloodReserve <= 0f) return;
            AddBloodSource(ArteryConfig.BloodSpurtStrength * 0.5f, bodyPart,
                CorpseBleedDuration, 0.25f, false);
            enabled = true;
        }

        // ---- simulation ------------------------------------------------------

        private void Update()
        {
            UpdateBruising();
            UpdateBlood();

            if (_health == null || !_health.IsAlive)
            {
                DisableWhenIdle();
                return;
            }

            ApplyPendingTreatments();
            if (_tracks.Count == 0)
            {
                DisableWhenIdle();
                return;
            }

            _tickAccumulator += Time.deltaTime;
            if (_tickAccumulator < TickStep) return;
            float deltaTime = _tickAccumulator;
            _tickAccumulator = 0f;

            ExpireClottedWounds();
            ArteryProfile profile = ArteryProfile.Resolve(_player);
            // Every additional severed artery escalates the whole stack, so
            // the third wound speeds up the first two as well.
            float multiplier = GetBleedRateMultiplier() *
                profile.GetEscalation(ArterialWoundCount);

            for (int i = 0; i < ArterialBodyParts.Length; i++)
            {
                EBodyPart bodyPart = ArterialBodyParts[i];
                if (!_tracks.TryGetValue(bodyPart, out ArteryTrack track) ||
                    !track.Active) continue;
                float damage = track.GetDamagePerSecond() * multiplier *
                    deltaTime;
                if (damage <= 0f) continue;

                DamageInfo bleedDamageInfo = BuildBleedDamageInfo(track);

                // Systemic blood loss drains straight to the chest. Vanilla
                // only dies from a destroyed head or chest, so without this a
                // limb artery is absorbed by the limb and the stomach and can
                // never kill.
                if (profile.SystemicFraction > 0f)
                {
                    ApplyBypassedDamage(EBodyPart.Chest,
                        damage * profile.SystemicFraction, bleedDamageInfo, 0);
                    if (!IsHealthAlive()) return;
                }

                ApplyPrimaryAndShared(bodyPart,
                    damage * (1f - profile.SystemicFraction),
                    bleedDamageInfo);
                if (!IsHealthAlive()) return;
            }

            EnsureMarkers();
            UpdateNativeBleedPresentation();
            LogBleedDiagnostic(profile, multiplier);
        }

        /// <summary>
        /// Once a second, dump where the blood loss is actually going. The
        /// requested-versus-accepted split matters: the health controller can
        /// silently absorb or redistribute what it is handed, so a healthy
        /// requested rate does not by itself prove damage is landing.
        /// </summary>
        private void LogBleedDiagnostic(in ArteryProfile profile,
            float multiplier)
        {
            if (!ArteryConfig.LogBleedTicks.Value ||
                Time.unscaledTime < _nextDiagnosticTime) return;
            _nextDiagnosticTime = Time.unscaledTime + 1f;

            float rawRate = 0f;
            foreach (KeyValuePair<EBodyPart, ArteryTrack> pair in _tracks)
                rawRate += pair.Value.GetDamagePerSecond();

            System.Text.StringBuilder landed = new System.Text.StringBuilder();
            for (int i = 0; i < ArterialBodyParts.Length; i++)
            {
                EBodyPart part = ArterialBodyParts[i];
                if (!_acceptedByPart.TryGetValue(part, out float taken) ||
                    taken <= 0.01f) continue;
                landed.Append(part).Append('=').Append(taken.ToString("0.0"))
                    .Append(' ');
            }

            ArteryLog.Info(string.Format(
                "[BleedTick] {0} wounds={1} esc={2:0.00}x raw={3:0.00} HP/s " +
                "applied={4:0.00} HP/s systemic={5:P0} | requested={6:0.0} " +
                "accepted={7:0.0} HP | landed: {8}| pools head={9:0}/{10:0} " +
                "chest={11:0}/{12:0} stomach={13:0}/{14:0} Lleg={15:0}/{16:0} " +
                "Rleg={17:0}/{18:0} destroyed(Lleg={19} Rleg={20} stomach={21})",
                profile.Name, ArterialWoundCount,
                profile.GetEscalation(ArterialWoundCount),
                rawRate, rawRate * multiplier, profile.SystemicFraction,
                _requestedDamageTotal, _acceptedDamageTotal,
                landed.Length == 0 ? "NOTHING " : landed.ToString(),
                Pool(EBodyPart.Head).Current, Pool(EBodyPart.Head).Maximum,
                Pool(EBodyPart.Chest).Current, Pool(EBodyPart.Chest).Maximum,
                Pool(EBodyPart.Stomach).Current, Pool(EBodyPart.Stomach).Maximum,
                Pool(EBodyPart.LeftLeg).Current, Pool(EBodyPart.LeftLeg).Maximum,
                Pool(EBodyPart.RightLeg).Current,
                Pool(EBodyPart.RightLeg).Maximum,
                _health.IsBodyPartDestroyed(EBodyPart.LeftLeg),
                _health.IsBodyPartDestroyed(EBodyPart.RightLeg),
                _health.IsBodyPartDestroyed(EBodyPart.Stomach)));
        }

        private ValueStruct Pool(EBodyPart bodyPart) =>
            _health.GetBodyPartHealth(bodyPart);

        /// <summary>
        /// Vanilla heavy-bleeding damage, re-stamped with the shooter so the
        /// bleed-out is attributed to them rather than to nobody.
        /// </summary>
        private static DamageInfo BuildBleedDamageInfo(ArteryTrack track)
        {
            DamageInfo damageInfo = DamageHelper.HeavyBleedingDamage;
            if (track.Aggressor != null) damageInfo.Player = track.Aggressor;
            return damageInfo;
        }

        private void ExpireClottedWounds()
        {
            for (int i = 0; i < ArterialBodyParts.Length; i++)
            {
                EBodyPart bodyPart = ArterialBodyParts[i];
                if (!_tracks.TryGetValue(bodyPart, out ArteryTrack track))
                    continue;
                track.RemoveClottedWounds();
                if (track.Active) continue;
                RemoveMarker(bodyPart);
                _tracks.Remove(bodyPart);
            }
        }

        private float GetBleedRateMultiplier() =>
            _health != null && _health.HasBloodLossBlockers()
                ? BloodLossStimBleedMultiplier : 1f;

        private bool IsHealthAlive() => _health != null && _health.IsAlive;

        private bool HasRecurringWork()
        {
            bool hasBleed = _health != null && _health.IsAlive &&
                _tracks.Count > 0;
            return hasBleed ||
                   _bruiseStrength > 0f ||
                   !Mathf.Approximately(_appliedRestorePenalty, 0f) ||
                   _bloodSources.Count > 0 ||
                   _bloodParticles.Count > 0;
        }

        private void DisableWhenIdle()
        {
            if (!HasRecurringWork()) enabled = false;
        }

        // ---- damage application and linkage ----------------------------------

        private float GetShareFraction(EBodyPart source)
        {
            ValueStruct health = _health.GetBodyPartHealth(source);
            float ratio = health.Maximum > 0f
                ? Mathf.Clamp01(health.Current / health.Maximum) : 0f;
            return Mathf.Lerp(1f, 0.25f, ratio);
        }

        private float EstimateLinkedDamageMultiplier(EBodyPart source)
        {
            if (!ArteryConfig.EnableArterialLinkage.Value) return 1f;
            EBodyPart[] targets = GetSharedTargets(source);
            if (targets == null || targets.Length == 0) return 1f;
            return 1f + GetShareFraction(source) *
                ArteryConfig.GetLinkageMultiplier(source);
        }

        /// <summary>
        /// Applies blood loss to the wounded part, then bleeds a share of it
        /// into the neighbouring parts. Without this a femoral bleed would only
        /// black the leg and stop, because vanilla never applies further damage
        /// to a destroyed limb.
        /// </summary>
        private void ApplyPrimaryAndShared(EBodyPart source, float damage,
            DamageInfo damageInfo)
        {
            if (damage <= 0f || !IsHealthAlive()) return;

            if (!ArteryConfig.EnableArterialLinkage.Value)
            {
                if (!_health.IsBodyPartDestroyed(source))
                    ApplyBleedDamage(source, damage, damageInfo);
                return;
            }

            if (_health.IsBodyPartDestroyed(source))
            {
                EBodyPart bypass = GetBypassTarget(source);
                if (bypass != EBodyPart.Common)
                    ApplyBypassedDamage(bypass,
                        damage * ArteryConfig.GetLinkageMultiplier(source),
                        damageInfo, 1);
                return;
            }

            float sharedPool = damage * GetShareFraction(source) *
                ArteryConfig.GetLinkageMultiplier(source);
            ApplyBleedDamage(source, damage, damageInfo);
            if (!IsHealthAlive()) return;

            EBodyPart[] targets = GetSharedTargets(source);
            if (targets == null || targets.Length == 0 || sharedPool <= 0f)
                return;
            float perTarget = sharedPool / targets.Length;
            for (int i = 0; i < targets.Length; i++)
            {
                if (!IsHealthAlive()) break;
                ApplyBypassedDamage(targets[i], perTarget, damageInfo, 0);
            }
        }

        private void ApplyBypassedDamage(EBodyPart target, float damage,
            DamageInfo damageInfo, int crossedBlackedParts)
        {
            if (damage <= 0f || !IsHealthAlive()) return;

            int guard = 0;
            while (IsHealthAlive() && target != EBodyPart.Common &&
                _health.IsBodyPartDestroyed(target) && guard++ < 4)
            {
                crossedBlackedParts++;
                EBodyPart next = GetBypassTarget(target);
                if (next == target) return;
                target = next;
            }

            if (!IsHealthAlive() || target == EBodyPart.Common ||
                _health.IsBodyPartDestroyed(target)) return;
            ApplyBleedDamage(target,
                damage * ArteryConfig.GetBlackedRetention(crossedBlackedParts),
                damageInfo);
        }

        private void ApplyBleedDamage(EBodyPart bodyPart, float damage,
            DamageInfo damageInfo)
        {
            if (damage <= 0f || !IsHealthAlive()) return;

            float now = Time.unscaledTime;
            bool allowPresentation = now >= _nextPresentationTime;
            if (allowPresentation)
                _nextPresentationTime = now + GetPresentationInterval();

            bool previousInside = BleedPresentationContext.InsideBleedDamage;
            bool previousAllowance = BleedPresentationContext.AllowPresentation;
            BleedPresentationContext.InsideBleedDamage = true;
            BleedPresentationContext.AllowPresentation = allowPresentation;
            _bleedDeathVoicePending = true;
            try
            {
                // The return value is what the health controller actually
                // accepted, which is not necessarily what was asked for.
                _requestedDamageTotal += damage;
                float accepted = Mathf.Abs(
                    _health.ApplyDamage(bodyPart, damage, damageInfo));
                _acceptedDamageTotal += accepted;
                if (!_acceptedByPart.ContainsKey(bodyPart))
                    _acceptedByPart[bodyPart] = 0f;
                _acceptedByPart[bodyPart] += accepted;
            }
            catch (System.NullReferenceException) when (!IsHealthAlive())
            {
                // EFT tears down health state mid-kill; nothing left to damage.
            }
            finally
            {
                BleedPresentationContext.InsideBleedDamage = previousInside;
                BleedPresentationContext.AllowPresentation = previousAllowance;
                _bleedDeathVoicePending = false;
            }
        }

        private float GetPresentationInterval()
        {
            int wounds = Mathf.Clamp(ArterialWoundCount, 1,
                MaximumPresentationBleedCount);
            return Mathf.Lerp(MaximumBleedPresentationInterval,
                MinimumBleedPresentationInterval,
                (wounds - 1f) / (MaximumPresentationBleedCount - 1f));
        }

        private static EBodyPart[] GetSharedTargets(EBodyPart source)
        {
            switch (source)
            {
                case EBodyPart.Head: return HeadSharedTargets;
                case EBodyPart.Chest: return ChestSharedTargets;
                case EBodyPart.Stomach: return StomachSharedTargets;
                case EBodyPart.LeftArm:
                case EBodyPart.RightArm: return ArmSharedTargets;
                case EBodyPart.LeftLeg:
                case EBodyPart.RightLeg: return LegSharedTargets;
                default: return null;
            }
        }

        private static EBodyPart GetBypassTarget(EBodyPart source)
        {
            switch (source)
            {
                case EBodyPart.LeftLeg:
                case EBodyPart.RightLeg: return EBodyPart.Stomach;
                case EBodyPart.LeftArm:
                case EBodyPart.RightArm:
                case EBodyPart.Stomach: return EBodyPart.Chest;
                default: return EBodyPart.Common;
            }
        }

        // ---- native bleed markers --------------------------------------------

        /// <summary>
        /// Every arterial wound is surfaced as a real vanilla Heavy Bleeding
        /// effect, so the health panel, the bleed icon and every vanilla
        /// treatment item behave exactly as the player expects.
        /// </summary>
        private void EnsureMarkers()
        {
            if (_health == null || _addingMarker) return;
            // While a blood-loss stim is active EFT strips bleed markers as
            // fast as they are added; do not fight it.
            if (_health.HasBloodLossBlockers()) return;
            _addingMarker = true;
            try
            {
                foreach (KeyValuePair<EBodyPart, ArteryTrack> pair in _tracks)
                {
                    if (!pair.Value.Active) continue;
                    ActiveHealthController.LightBleeding light =
                        _health.FindExistingEffect<
                            ActiveHealthController.LightBleeding>(pair.Key);
                    if (light != null) light.ForceRemove();
                    ActiveHealthController.HeavyBleeding heavy =
                        _health.FindExistingEffect<
                            ActiveHealthController.HeavyBleeding>(pair.Key);
                    if (heavy == null)
                    {
                        _health.DoBleed<
                            ActiveHealthController.HeavyBleeding>(pair.Key);
                        heavy = _health.FindExistingEffect<
                            ActiveHealthController.HeavyBleeding>(pair.Key);
                    }
                    // Silence the damage the native bleed would apply itself
                    // the moment it exists, otherwise it double-dips for one
                    // frame before UpdateNativeBleedPresentation runs.
                    if (heavy != null) heavy.float_15 = 0f;
                }
            }
            finally { _addingMarker = false; }
        }

        private void RemoveMarker(EBodyPart bodyPart)
        {
            for (int i = _bloodSources.Count - 1; i >= 0; i--)
                if (_bloodSources[i].BodyPart == bodyPart)
                    _bloodSources.RemoveAt(i);

            if (_health == null) return;
            _addingMarker = true;
            try
            {
                ActiveHealthController.HeavyBleeding heavy =
                    _health.FindExistingEffect<
                        ActiveHealthController.HeavyBleeding>(bodyPart);
                heavy?.ForceRemove();
            }
            finally { _addingMarker = false; }

            ArteryLog.Info("[Artery] Arterial wound clotted on " + bodyPart);
        }

        /// <summary>
        /// Zeroes the damage the native bleed would apply itself (ArteryCore
        /// owns the damage curve) and feeds the real rate and remaining time
        /// into the effect tooltip.
        /// </summary>
        private void UpdateNativeBleedPresentation()
        {
            if (_health == null) return;
            float multiplier = GetBleedRateMultiplier();
            foreach (KeyValuePair<EBodyPart, ArteryTrack> pair in _tracks)
            {
                ActiveHealthController.HeavyBleeding heavy =
                    _health.FindExistingEffect<
                        ActiveHealthController.HeavyBleeding>(pair.Key);
                if (heavy == null) continue;
                heavy.float_15 = 0f;
                NativeEffectLabels.UpdateArterialBleed(heavy,
                    pair.Value.GetTimeLeft(),
                    pair.Value.GetDamagePerSecond() * multiplier,
                    pair.Value.GetDominantZone());
            }
        }

        // ---- treatment -------------------------------------------------------

        private void OnEffectRemoved(IHealthEffect effect)
        {
            if (_addingMarker || effect == null) return;
            if (effect is ILightBleeding || effect is IHeavyBleeding)
                QueueTreatment(effect);
        }

        private void OnEffectResidual(IHealthEffect effect)
        {
            if (_addingMarker || effect == null) return;
            if (effect is ILightBleeding || effect is IHeavyBleeding)
                QueueTreatment(effect);
        }

        private void QueueTreatment(IHealthEffect effect)
        {
            if (!_queuedTreatmentEffects.Add(effect)) return;
            _pendingTreatments.Add(effect);
            enabled = true;
        }

        private void ApplyPendingTreatments()
        {
            for (int i = 0; i < _pendingTreatments.Count; i++)
                ApplyTreatment(_pendingTreatments[i]);
            _pendingTreatments.Clear();
            _queuedTreatmentEffects.Clear();
        }

        private void ApplyTreatment(IHealthEffect removedEffect)
        {
            if (_health == null || _addingMarker || removedEffect == null)
                return;
            EBodyPart bodyPart = removedEffect.BodyPart;
            if (!_tracks.TryGetValue(bodyPart, out ArteryTrack track) ||
                !track.Active) return;

            float clotProgress =
                ArteryProfile.Resolve(_player).TreatmentClotProgress;
            track.AdvanceClotting(clotProgress);
            if (!track.Active)
            {
                RemoveMarker(bodyPart);
                _tracks.Remove(bodyPart);
            }
            else EnsureMarkers();

            ArteryLog.Info(string.Format(
                "[Artery] Treatment advanced clotting on {0} by {1:0}%",
                bodyPart, clotProgress * 100f));
        }

        // ---- bruising --------------------------------------------------------

        private void UpdateBruising()
        {
            if (_bruiseStrength <= 0f &&
                Mathf.Approximately(_appliedRestorePenalty, 0f)) return;
            if (_player == null || _player.Physical == null) return;

            if (!Mathf.Approximately(_appliedRestorePenalty, 0f))
                _player.Physical.RestoreRateBuff -= _appliedRestorePenalty;

            float duration = Mathf.Max(0.01f, ArteryConfig.BruiseDuration.Value);
            float remaining = Mathf.Clamp01(
                (_bruiseExpires - Time.unscaledTime) / duration);
            _currentBruiseStrength = _bruiseStrength * remaining;
            if (remaining <= 0f)
            {
                _bruiseStrength = 0f;
                _bruiseUiEffect = null;
            }

            _appliedRestorePenalty = -_player.Physical.StaminaRestoreRate *
                ArteryConfig.BruiseStaminaPenalty.Value *
                _currentBruiseStrength;
            _player.Physical.RestoreRateBuff += _appliedRestorePenalty;
        }

        // ---- blood spray -----------------------------------------------------

        private void AddBloodSource(float severity, EBodyPart bodyPart,
            float duration, float frontLoad, bool passedThrough)
        {
            if (_player == null || severity <= 0f ||
                !ArteryConfig.EnableBloodEffects.Value) return;
            enabled = true;

            Transform attachment = _lastImpactTransform != null
                ? _lastImpactTransform : _player.gameObject.transform;
            if (attachment == null) return;

            if (_bloodSources.Count >= MaximumBloodSources)
                _bloodSources.RemoveAt(0);

            _bloodSources.Add(new BloodSource
            {
                Attachment = attachment,
                LocalPosition =
                    attachment.InverseTransformPoint(_lastImpactPoint),
                LocalDirection = (Quaternion.Inverse(attachment.rotation) *
                    -_lastImpactDirection).normalized,
                Strength = severity,
                Created = Time.unscaledTime,
                Duration = Mathf.Max(0.01f, duration),
                FrontLoad = frontLoad,
                BodyPart = bodyPart
            });

            // A through-and-through sprays from the exit side as well.
            if (!passedThrough || _bloodSources.Count >= MaximumBloodSources)
                return;
            _bloodSources.Add(new BloodSource
            {
                Attachment = attachment,
                LocalPosition =
                    attachment.InverseTransformPoint(_lastImpactPoint),
                LocalDirection = (Quaternion.Inverse(attachment.rotation) *
                    _lastImpactDirection).normalized,
                Strength = severity * 0.6f,
                Created = Time.unscaledTime,
                Duration = Mathf.Max(0.01f, duration),
                FrontLoad = frontLoad,
                BodyPart = bodyPart
            });
        }

        private void UpdateBlood()
        {
            if (!ArteryConfig.EnableBloodEffects.Value)
            {
                _bloodSources.Clear();
                _bloodParticles.Clear();
                return;
            }

            float now = Time.unscaledTime;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            SimulateBloodParticles(now, dt);

            if (_player == null || _health == null) return;
            bool alive = _health.IsAlive;
            if (_wasAlive && !alive && !_corpseBloodInitialized)
            {
                EnsureCorpseBloodReserve();
                for (int i = 0; i < _bloodSources.Count; i++)
                    _bloodSources[i].Created = now;
            }
            _wasAlive = alive;
            if (!alive && _corpseBloodReserve <= 0f) return;

            float totalRate = 0f;
            for (int i = _bloodSources.Count - 1; i >= 0; i--)
            {
                BloodSource source = _bloodSources[i];
                if (source.Attachment == null)
                {
                    _bloodSources.RemoveAt(i);
                    continue;
                }

                float corpseDecay = 1f;
                if (!alive)
                {
                    corpseDecay = 1f - Mathf.Clamp01(
                        (now - source.Created) / CorpseBleedDuration);
                    if (corpseDecay <= 0f)
                    {
                        _bloodSources.RemoveAt(i);
                        continue;
                    }
                }

                float strength = ArteryConfig.GetBleedStrength(
                    now - source.Created, source.Duration, source.FrontLoad);
                if (strength <= 0f && alive)
                {
                    _bloodSources.RemoveAt(i);
                    continue;
                }

                float rate = source.Strength * strength * corpseDecay;
                totalRate += rate;
                source.EmissionAccumulator += Mathf.Clamp(rate * 0.55f, 0.5f,
                    24f) * dt;
                int count = Mathf.Min(3,
                    Mathf.FloorToInt(source.EmissionAccumulator));
                source.EmissionAccumulator -= count;

                Vector3 origin =
                    source.Attachment.TransformPoint(source.LocalPosition);
                Vector3 outward =
                    source.Attachment.rotation * source.LocalDirection;
                for (int n = 0; n < count &&
                    _bloodParticles.Count < MaximumBloodParticles; n++)
                    SpawnBloodParticle(origin, outward, rate);
            }

            if (!alive)
            {
                _corpseBloodReserve = Mathf.Max(0f,
                    _corpseBloodReserve - totalRate * dt);
                if (_corpseBloodReserve <= 0f) _bloodSources.Clear();
            }
        }

        private void SimulateBloodParticles(float now, float dt)
        {
            for (int i = _bloodParticles.Count - 1; i >= 0; i--)
            {
                BloodParticle particle = _bloodParticles[i];
                if (particle.Expires <= now)
                {
                    _bloodParticles.RemoveAt(i);
                    continue;
                }

                Vector3 previous = particle.Position;
                particle.Velocity += Physics.gravity * dt;
                Vector3 next = particle.Position + particle.Velocity * dt;
                Vector3 travel = next - previous;
                if (travel.sqrMagnitude > 0.000001f && Physics.Raycast(previous,
                    travel.normalized, out RaycastHit hit, travel.magnitude,
                    EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK))
                {
                    if (now >= _nextBloodDecalTime &&
                        Singleton<Effects>.Instantiated)
                    {
                        Singleton<Effects>.Instance.EmitBleeding(hit.point,
                            hit.normal);
                        _nextBloodDecalTime = now + 0.10f;
                    }
                    _bloodParticles.RemoveAt(i);
                    continue;
                }

                particle.Position = next;
                _bloodParticles[i] = particle;
            }
        }

        private void SpawnBloodParticle(Vector3 origin, Vector3 outward,
            float rate)
        {
            float speed = Mathf.Lerp(0.20f, 0.82f, Mathf.Clamp01(rate / 40f));
            Vector3 velocity = outward * speed +
                Random.insideUnitSphere * 0.10f +
                Vector3.up * Random.Range(0.03f, 0.16f);
            _bloodParticles.Add(new BloodParticle
            {
                Position = origin + outward * 0.012f,
                Velocity = velocity,
                Expires = Time.unscaledTime + Random.Range(0.8f, 1.45f),
                Size = Random.Range(0.007f, 0.014f),
                TrailLength = Mathf.Clamp(velocity.magnitude *
                    Random.Range(0.045f, 0.085f), 0.012f, 0.065f)
            });
        }

        private void EnsureCorpseBloodReserve()
        {
            if (_corpseBloodInitialized || _health == null) return;
            float total = 0f;
            for (int i = 0; i < ArterialBodyParts.Length; i++)
                total += Mathf.Max(0f,
                    _health.GetBodyPartHealth(ArterialBodyParts[i]).Current);
            _corpseBloodReserve = total;
            _corpseBloodInitialized = true;
        }

        // ---- teardown --------------------------------------------------------

        private void Unsubscribe()
        {
            if (_subscribed && _health != null)
            {
                _health.EffectRemovedEvent -= OnEffectRemoved;
                _health.EffectResidualEvent -= OnEffectResidual;
            }
            _subscribed = false;
            _queuedTreatmentEffects.Clear();
            _pendingTreatments.Clear();
        }

        private void OnDestroy()
        {
            if (_player != null && _player.Physical != null &&
                !Mathf.Approximately(_appliedRestorePenalty, 0f))
                _player.Physical.RestoreRateBuff -= _appliedRestorePenalty;
            _appliedRestorePenalty = 0f;
            _bloodSources.Clear();
            _bloodParticles.Clear();
            _tracks.Clear();
            Unsubscribe();
        }
    }
}
