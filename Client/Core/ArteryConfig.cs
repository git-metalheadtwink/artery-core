using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using UnityEngine;

namespace ArteryCore
{
    /// <summary>
    /// Every tunable value in ArteryCore. Bound once from Plugin.Awake.
    /// Arterial bleeding is tuned separately for humans and for AI; see
    /// <see cref="ArteryProfile"/>.
    /// </summary>
    internal static class ArteryConfig
    {
        // ---- Feature toggles -------------------------------------------------
        internal static ConfigEntry<bool> EnableArteries, EnableBoneFractures,
            EnableBruising, EnableHitPressure, EnablePresentation,
            EnableBloodEffects, EnablePersistentBloodDecals,
            EnableBleedKillCredit;

        // ---- Artery gating ---------------------------------------------------
        internal static ConfigEntry<float> ArteryMinimumDepthRatio;

        // ---- Bleeding: players -----------------------------------------------
        private static ConfigEntry<float> PlayerBleedBudget, PlayerBleedDuration,
            PlayerFrontLoad, PlayerSystemicFraction, PlayerEscalation,
            PlayerMaximumEscalation, PlayerTreatmentClot, PlayerChanceMultiplier,
            PlayerArterialMultiplier;
        private static ConfigEntry<int> PlayerStackCap;

        // ---- Bleeding: AI ----------------------------------------------------
        private static ConfigEntry<float> AiBleedBudget, AiBleedDuration,
            AiFrontLoad, AiSystemicFraction, AiEscalation, AiMaximumEscalation,
            AiTreatmentClot, AiChanceMultiplier, ScavArterialMultiplier,
            PmcBotArterialMultiplier;
        private static ConfigEntry<int> AiStackCap;

        // ---- Linkage ---------------------------------------------------------
        internal static ConfigEntry<bool> EnableArterialLinkage;
        internal static ConfigEntry<float> ArmLinkageMultiplier,
            LegLinkageMultiplier, StomachLinkageMultiplier,
            OneBlackedRetention, TwoBlackedRetention, ThreePlusBlackedRetention;

        // ---- Bone fractures --------------------------------------------------
        internal static ConfigEntry<bool> FractureLimbBones, FractureSpine,
            SpineFractureSpeedLimit;
        internal static ConfigEntry<float> FracturePainStrength,
            FracturePainDuration, ArmBoneRadius, LegBoneRadius,
            CervicalSpineRadius, ThoracicSpineRadius;

        // ---- Bruising --------------------------------------------------------
        internal static ConfigEntry<float> BruiseDuration,
            BruiseStaminaPenalty, BruiseSpeedPenalty;

        // ---- Impact shock ----------------------------------------------------
        internal static ConfigEntry<float> HitPressureIntensityPerHit,
            HitPressureMaximumIntensity, HitPressureFadeSeconds,
            HitPressurePainDuration;

        // ---- Target balance --------------------------------------------------
        internal static ConfigEntry<bool> ApplyToPlayers, ApplyToAI;

        // ---- Debugging -------------------------------------------------------
        internal static ConfigEntry<bool> DebugLogging, LogEveryShot,
            ForceArteryHits, LogBleedTicks;

        // ---- Fixed constants -------------------------------------------------
        internal const float BleedRapidClotDuration = 3f;
        internal const float BleedRapidClotStrength = 0.15f;
        internal const float BloodSpurtStrength = 6f;

        private static readonly Dictionary<ArteryZoneKind, ConfigEntry<bool>>
            ZoneEnabled = new Dictionary<ArteryZoneKind, ConfigEntry<bool>>();
        private static readonly Dictionary<ArteryZoneKind, ConfigEntry<float>>
            ZoneChance = new Dictionary<ArteryZoneKind, ConfigEntry<float>>();
        private static readonly Dictionary<ArteryZoneKind, ConfigEntry<float>>
            ZoneSeverity = new Dictionary<ArteryZoneKind, ConfigEntry<float>>();

        internal static void Initialize(ConfigFile config)
        {
            BindFeatureToggles(config);
            BindArteryZones(config);
            BindPlayerBleeding(config);
            BindAiBleeding(config);
            BindBoneFractures(config);
            BindBruising(config);
            BindHitPressure(config);
            BindLinkage(config);
            BindTargetBalance(config);
            BindDebugging(config);
        }

        // ---- profiles --------------------------------------------------------

        internal static ArteryProfile BuildPlayerProfile() =>
            new ArteryProfile("player",
                PlayerBleedBudget.Value, PlayerBleedDuration.Value,
                PlayerFrontLoad.Value, PlayerSystemicFraction.Value,
                PlayerEscalation.Value, PlayerMaximumEscalation.Value,
                PlayerStackCap.Value, PlayerTreatmentClot.Value,
                PlayerChanceMultiplier.Value, PlayerArterialMultiplier.Value);

        internal static ArteryProfile BuildAiProfile(bool isScav) =>
            new ArteryProfile(isScav ? "scav" : "pmc-bot",
                AiBleedBudget.Value, AiBleedDuration.Value, AiFrontLoad.Value,
                AiSystemicFraction.Value, AiEscalation.Value,
                AiMaximumEscalation.Value, AiStackCap.Value,
                AiTreatmentClot.Value, AiChanceMultiplier.Value,
                isScav ? ScavArterialMultiplier.Value
                       : PmcBotArterialMultiplier.Value);

        // =====================================================================

        private static void BindFeatureToggles(ConfigFile config)
        {
            EnableArteries = Toggle(config, "Arteries", true,
                "Arterial Bleeding", 100,
                "Master switch for artery-hitbox detection and arterial bleeding.");
            EnableBoneFractures = Toggle(config, "BoneFractures", true,
                "Anatomical Bone Fractures", 90,
                "Deterministic fractures when the bullet path strikes a bone capsule.");
            EnableBruising = Toggle(config, "Bruising", true,
                "Bruising", 80,
                "Apply bruising when armor fully stops a bullet.");
            EnableHitPressure = Toggle(config, "HitPressure", true,
                "Impact Shock", 70,
                "Screen vignette and short pain effect when you are hit.");
            EnablePresentation = Toggle(config, "Presentation", true,
                "Bleed Audio / Screen Effects", 60,
                "Throttle repeated bleed-tick audio and screen blood, and play agony death voices.");
            EnableBloodEffects = Toggle(config, "BloodEffects", true,
                "Procedural World Blood", 50,
                "Render arterial blood spray particles that stain the world.");
            EnablePersistentBloodDecals = Toggle(config, "PersistentBloodDecals",
                true, "Expanded Blood Decals", 40,
                "Increase the persistent blood-decal capacity of the base game.");
            EnableBleedKillCredit = Toggle(config, "BleedKillCredit", true,
                "Credit Bleed-Out Kills", 30,
                "Attribute arterial bleed-out kills and quest progress to the shooter.");
        }

        private static void BindArteryZones(ConfigFile config)
        {
            // Thigh 6/10, forearm 4/10, neck 8/10 as specified. Brachial and
            // groin are wired up but off, so the live set is the three named
            // zones. Each profile scales these by its own chance multiplier.
            BindZone(config, ArteryZoneKind.Carotid,
                "Carotid / Jugular (neck)", true, 0.80f, 1.35f, 100,
                "NeckFront and NeckBack colliders. Verified in-game to drain " +
                "the CHEST pool, not the head, which makes this the only " +
                "artery wired directly to a pool that kills.");
            BindZone(config, ArteryZoneKind.Femoral,
                "Femoral (thigh)", true, 0.60f, 1.15f, 90,
                "LeftThigh and RightThigh colliders.");
            BindZone(config, ArteryZoneKind.Radial,
                "Radial / Ulnar (forearm)", true, 0.40f, 0.80f, 80,
                "LeftForearm and RightForearm colliders. Also covers the hand.");
            BindZone(config, ArteryZoneKind.Brachial,
                "Brachial (upper arm)", false, 0.35f, 0.95f, 70,
                "LeftUpperArm and RightUpperArm colliders. Off by default.");
            BindZone(config, ArteryZoneKind.FemoralGroin,
                "Femoral at groin (pelvis)", false, 0.50f, 1.30f, 60,
                "Pelvis and PelvisBack colliders. Drains the stomach pool. Off by default.");

            ArteryMinimumDepthRatio = Float(config, "Artery Zones",
                "MinimumDepthRatio", 0.15f, "02 - Artery Zones",
                "Minimum Wound Depth", 10, 0f, 1f,
                "Fraction of the limb thickness the bullet must travel before " +
                "an artery can be severed. Stops grazes from cutting a carotid.");
        }

        private static void BindZone(ConfigFile config, ArteryZoneKind kind,
            string displayName, bool enabledDefault, float chanceDefault,
            float severityDefault, int order, string description)
        {
            string section = "Artery Zone - " + kind;
            ZoneEnabled[kind] = config.Bind(section, "Enabled", enabledDefault,
                Ui(description, "02 - Artery Zones", displayName, order));
            ZoneChance[kind] = config.Bind(section, "Chance", chanceDefault,
                Ui("Base chance per penetrating bullet that this artery is " +
                   "severed, before the per-profile chance multiplier.",
                    "02 - Artery Zones", displayName + " - Chance", order - 1,
                    new AcceptableValueRange<float>(0f, 1f)));
            ZoneSeverity[kind] = config.Bind(section, "Severity",
                severityDefault,
                Ui("Multiplier on the bleed rate and total blood loss of this artery.",
                    "02 - Artery Zones", displayName + " - Severity", order - 2,
                    new AcceptableValueRange<float>(0.1f, 3f)));
        }

        internal static bool IsZoneEnabled(ArteryZoneKind kind) =>
            ZoneEnabled.TryGetValue(kind, out ConfigEntry<bool> entry) &&
            entry.Value;

        internal static float GetZoneChance(ArteryZoneKind kind) =>
            ZoneChance.TryGetValue(kind, out ConfigEntry<float> entry)
                ? Mathf.Clamp01(entry.Value) : 0f;

        internal static float GetZoneSeverity(ArteryZoneKind kind) =>
            ZoneSeverity.TryGetValue(kind, out ConfigEntry<float> entry)
                ? Mathf.Max(0.01f, entry.Value) : 1f;

        // ---- bleeding: players ------------------------------------------------

        private static void BindPlayerBleeding(ConfigFile config)
        {
            const string section = "Bleeding - Players";
            const string category = "03 - Bleeding (Players)";

            PlayerBleedBudget = Float(config, section, "BleedBudget", 1.15f,
                category, "Total Blood Loss", 100, 0.1f, 5f,
                "Total HP an arterial wound bleeds out, as a multiple of the " +
                "post-armor damage of the bullet. Bullet damage itself is " +
                "never reduced, so this is always damage on top of vanilla.");
            PlayerBleedDuration = Float(config, section, "BleedDuration", 25f,
                category, "Bleed Duration", 90, 3f, 180f,
                "Seconds an untreated arterial wound bleeds before it clots.");
            PlayerFrontLoad = Float(config, section, "FrontLoad", 0.25f,
                category, "Front Load", 80, 0f, 1f,
                "0 = flat bleed rate for the whole duration. 1 = most of the " +
                "blood loss lands in the first few seconds.");
            PlayerSystemicFraction = Float(config, section, "SystemicFraction",
                0f, category, "Systemic Blood Loss", 70, 0f, 1f,
                "Share of each arterial tick that drains straight to the " +
                "chest instead of into the wounded limb. This is the only " +
                "route by which a limb artery can kill, because vanilla only " +
                "dies from a destroyed head or chest. 0 keeps the shipped " +
                "behaviour where limb bleeds are survivable.");
            PlayerEscalation = Float(config, section, "Escalation", 0f,
                category, "Escalation Per Wound", 60, 0f, 1f,
                "Extra bleed rate per additional arterial wound on the same " +
                "target. The whole stack escalates, so a second and third " +
                "severed artery compound. 0 keeps the shipped behaviour.");
            PlayerMaximumEscalation = Float(config, section, "MaxEscalation",
                3f, category, "Escalation Ceiling", 50, 1f, 8f,
                "Hard cap on the escalation multiplier.");
            PlayerStackCap = config.Bind(section, "StackCap", 4,
                Ui("Maximum simultaneous arterial wounds counted per body " +
                   "part. Opening more evicts the oldest along with its " +
                   "unspent blood loss.",
                    category, "Stack Cap", 40,
                    new AcceptableValueRange<int>(1, 12)));
            PlayerTreatmentClot = Float(config, section,
                "TreatmentClotProgress", 1f, category,
                "Treatment Effectiveness", 30, 0f, 1f,
                "How much of an arterial wound a vanilla bandage closes. " +
                "1 = vanilla behaviour, the bleed stops outright.");
            PlayerChanceMultiplier = Float(config, section, "ChanceMultiplier",
                1f, category, "Artery Chance Multiplier", 20, 0f, 3f,
                "Scales every zone chance for human targets.");
            PlayerArterialMultiplier = Float(config, section,
                "ArterialMultiplier", 1f, category,
                "Arterial Damage Multiplier", 10, 0f, 5f,
                "Flat multiplier on all arterial blood loss dealt to humans.");
        }

        // ---- bleeding: AI -----------------------------------------------------

        private static void BindAiBleeding(ConfigFile config)
        {
            const string section = "Bleeding - AI";
            const string category = "04 - Bleeding (AI)";

            AiBleedBudget = Float(config, section, "BleedBudget", 1.15f,
                category, "Total Blood Loss", 100, 0.1f, 5f,
                "Total HP an arterial wound bleeds out of an AI target, as a " +
                "multiple of post-armor damage.");
            AiBleedDuration = Float(config, section, "BleedDuration", 12f,
                category, "Bleed Duration", 90, 3f, 180f,
                "Seconds an untreated arterial wound bleeds on an AI target. " +
                "Short, so the same blood loss arrives far faster than it " +
                "does on a human. Too short and the wound clots before it can " +
                "finish the job.");
            AiFrontLoad = Float(config, section, "FrontLoad", 0.20f,
                category, "Front Load", 80, 0f, 1f,
                "0 = flat bleed rate for the whole duration. 1 = most of the " +
                "blood loss lands in the first few seconds.");
            AiSystemicFraction = Float(config, section, "SystemicFraction",
                0.50f, category, "Systemic Blood Loss", 70, 0f, 1f,
                "Share of each arterial tick that drains straight to the " +
                "chest instead of into the wounded limb. The stomach is a 70 " +
                "HP sink that can never kill, so at 0.25 most of the blood " +
                "loss was disappearing into it and wounds clotted with the " +
                "chest still standing. Half is the measured value that lands " +
                "the kill.");
            AiEscalation = Float(config, section, "Escalation", 0.25f,
                category, "Escalation Per Wound", 60, 0f, 1f,
                "Extra bleed rate per additional arterial wound on the same " +
                "target. At 0.25 a three-wound target bleeds 50% faster and a " +
                "five-wound target twice as fast.");
            AiMaximumEscalation = Float(config, section, "MaxEscalation", 3f,
                category, "Escalation Ceiling", 50, 1f, 8f,
                "Hard cap on the escalation multiplier.");
            AiStackCap = config.Bind(section, "StackCap", 8,
                Ui("Maximum simultaneous arterial wounds counted per body " +
                   "part. Opening more evicts the oldest along with its " +
                   "unspent blood loss, so a low cap silently punishes " +
                   "concentrating fire on one limb. Escalation is bounded by " +
                   "its own ceiling, so this can sit high safely.",
                    category, "Stack Cap", 40,
                    new AcceptableValueRange<int>(1, 12)));
            AiTreatmentClot = Float(config, section, "TreatmentClotProgress",
                1f, category, "Treatment Effectiveness", 30, 0f, 1f,
                "How much of an arterial wound a bandage closes when a bot " +
                "heals itself. Lower this if bots trivially undo arterial hits.");
            AiChanceMultiplier = Float(config, section, "ChanceMultiplier",
                1.4f, category, "Artery Chance Multiplier", 20, 0f, 3f,
                "Scales every zone chance for AI targets. At 1.4 the 60% " +
                "femoral becomes 84%, so three thigh rounds reliably sever.");
            ScavArterialMultiplier = Float(config, section, "ScavMultiplier",
                1f, category, "Scav Blood Loss Multiplier", 15, 0f, 5f,
                "Scavs inherit the AI profile; this scales their blood loss.");
            PmcBotArterialMultiplier = Float(config, section,
                "PmcBotMultiplier", 1f, category,
                "PMC Bot Blood Loss Multiplier", 14, 0f, 5f,
                "PMC bots inherit the AI profile; this scales their blood loss.");
        }

        private static void BindLinkage(ConfigFile config)
        {
            EnableArterialLinkage = config.Bind("Linkage", "Enabled", true,
                Ui("Let arterial blood loss from a limb drain inward through " +
                   "neighbouring parts. Vanilla discards damage to a blacked " +
                   "limb, so without this a limb bleed dead-ends.",
                    "08 - Linkage", "Enable Linkage", 100));
            LegLinkageMultiplier = Float(config, "Linkage", "Leg", 1f,
                "08 - Linkage", "Leg Linkage", 90, 0f, 2f,
                "Share of leg arterial damage passed on to the stomach.");
            ArmLinkageMultiplier = Float(config, "Linkage", "Arm", 0.20f,
                "08 - Linkage", "Arm Linkage", 80, 0f, 2f,
                "Share of arm arterial damage passed on to the chest.");
            StomachLinkageMultiplier = Float(config, "Linkage", "Stomach", 0.75f,
                "08 - Linkage", "Stomach Linkage", 70, 0f, 2f,
                "Share of stomach arterial damage passed on outward.");
            OneBlackedRetention = Float(config, "Linkage", "OneBlacked", 0.95f,
                "08 - Linkage", "1 Blacked Part Retention", 60, 0f, 1f,
                "Damage retained after crossing one blacked body part.");
            TwoBlackedRetention = Float(config, "Linkage", "TwoBlacked", 0.80f,
                "08 - Linkage", "2 Blacked Parts Retention", 50, 0f, 1f,
                "Damage retained after crossing two blacked body parts.");
            ThreePlusBlackedRetention = Float(config, "Linkage",
                "ThreePlusBlacked", 0.50f, "08 - Linkage",
                "3+ Blacked Parts Retention", 40, 0f, 1f,
                "Damage retained after crossing three or more blacked parts.");
        }

        private static void BindBoneFractures(ConfigFile config)
        {
            FractureLimbBones = config.Bind("Bone Fractures", "LimbBones", true,
                Ui("Fracture the limb when the bullet path strikes the humerus, " +
                   "radius, femur or tibia capsule.",
                    "05 - Bone Fractures", "Limb Bone Fractures", 100));
            FractureSpine = config.Bind("Bone Fractures", "Spine", true,
                Ui("Fracture the chest or stomach when the bullet path strikes " +
                   "the spine capsule.",
                    "05 - Bone Fractures", "Spinal Fractures", 90));
            SpineFractureSpeedLimit = config.Bind("Bone Fractures",
                "SpineSpeedLimit", true,
                Ui("A spinal fracture caps movement speed and disables sprint " +
                   "until painkillers are taken.",
                    "05 - Bone Fractures", "Spinal Fracture Speed Limit", 80));
            FracturePainStrength = Float(config, "Bone Fractures",
                "PainStrength", 1f, "05 - Bone Fractures",
                "Fracture Pain Strength", 70, 0f, 2f,
                "Strength of the extra pain effect layered onto a bone fracture. " +
                "0 disables it and leaves vanilla fracture pain alone.");
            FracturePainDuration = Float(config, "Bone Fractures",
                "PainDuration", 8f, "05 - Bone Fractures",
                "Fracture Pain Duration", 60, 0f, 60f,
                "Seconds the extra fracture pain effect lasts.");
            ArmBoneRadius = Float(config, "Bone Fractures", "ArmBoneRadius",
                0.0103125f, "05 - Bone Fractures", "Arm Bone Radius", 30,
                0.002f, 0.05f, "Capsule radius of the arm bones, in metres.");
            LegBoneRadius = Float(config, "Bone Fractures", "LegBoneRadius",
                0.01875f, "05 - Bone Fractures", "Leg Bone Radius", 29,
                0.002f, 0.08f, "Capsule radius of the leg bones, in metres.");
            CervicalSpineRadius = Float(config, "Bone Fractures",
                "CervicalSpineRadius", 0.021875f, "05 - Bone Fractures",
                "Cervical Spine Radius", 28, 0.002f, 0.08f,
                "Capsule radius of the neck spine, in metres.");
            ThoracicSpineRadius = Float(config, "Bone Fractures",
                "ThoracicSpineRadius", 0.025f, "05 - Bone Fractures",
                "Thoracic Spine Radius", 27, 0.002f, 0.08f,
                "Capsule radius of the chest and lumbar spine, in metres.");
        }

        private static void BindBruising(ConfigFile config)
        {
            BruiseDuration = Float(config, "Bruising", "Duration", 15f,
                "06 - Bruising", "Bruise Duration", 100, 1f, 120f,
                "Seconds a bruise lasts after armor stops a bullet.");
            BruiseStaminaPenalty = Float(config, "Bruising", "StaminaPenalty",
                0.30f, "06 - Bruising", "Stamina Restore Penalty", 90, 0f, 1f,
                "Fraction of stamina regeneration lost at full bruise strength.");
            BruiseSpeedPenalty = Float(config, "Bruising", "SpeedPenalty",
                0.15f, "06 - Bruising", "Movement Speed Penalty", 80, 0f, 0.6f,
                "Fraction of movement speed lost at full bruise strength.");
        }

        private static void BindHitPressure(ConfigFile config)
        {
            HitPressureIntensityPerHit = Float(config, "Impact Shock",
                "IntensityPerHit", 0.05f, "07 - Impact Shock",
                "Intensity Per Hit", 100, 0f, 0.5f,
                "Vignette intensity added by each hit you take.");
            HitPressureMaximumIntensity = Float(config, "Impact Shock",
                "MaximumIntensity", 0.50f, "07 - Impact Shock",
                "Maximum Intensity", 90, 0f, 1f,
                "Ceiling on stacked vignette intensity.");
            HitPressureFadeSeconds = Float(config, "Impact Shock",
                "FadeSeconds", 1.50f, "07 - Impact Shock",
                "Fade Duration", 80, 0.1f, 10f,
                "Seconds for the vignette to fade back to clear.");
            HitPressurePainDuration = Float(config, "Impact Shock",
                "PainDuration", 1.50f, "07 - Impact Shock",
                "Pain Duration", 70, 0f, 10f,
                "Seconds the impact-shock pain effect lasts.");
        }

        private static void BindTargetBalance(ConfigFile config)
        {
            ApplyToPlayers = config.Bind("Target Balance", "ApplyToPlayers",
                true, Ui("Apply arteries, fractures and bruising to human players.",
                    "09 - Target Balance", "Affect Players", 100));
            ApplyToAI = config.Bind("Target Balance", "ApplyToAI", true,
                Ui("Apply arteries, fractures and bruising to AI and scavs.",
                    "09 - Target Balance", "Affect AI / Scavs", 90));
        }

        private static void BindDebugging(ConfigFile config)
        {
            DebugLogging = config.Bind("Debug", "Logging", false,
                Ui("Write ArteryCore diagnostics to the BepInEx log.",
                    "10 - Debugging", "Logging", 100));
            LogEveryShot = config.Bind("Debug", "LogEveryShot", false,
                Ui("Log the collider, depth and artery roll for every bullet.",
                    "10 - Debugging", "Log Every Shot", 90));
            ForceArteryHits = config.Bind("Debug", "ForceArteryHits", false,
                Ui("Treat every enabled artery zone as a guaranteed hit.",
                    "10 - Debugging", "Force Artery Hits (100%)", 80));
            // Deliberately a new key rather than reusing LogBleedTicks, so an
            // existing config file picks up the new default instead of keeping
            // the old value.
            LogBleedTicks = config.Bind("Debug", "BleedDiagnostics", true,
                Ui("Once a second, log the live bleed rate, every body part " +
                   "pool, and the damage the health controller actually " +
                   "accepted broken down per part. This is what shows where " +
                   "arterial blood loss really goes.",
                    "10 - Debugging", "Bleed Diagnostics (verbose)", 70));
        }

        // =====================================================================

        /// <summary>
        /// Area under the bleed decay curve, used to convert a total blood-loss
        /// budget into a starting HP/second rate.
        /// </summary>
        internal static float GetBleedDecayArea(float duration, float frontLoad)
        {
            duration = Mathf.Max(0.01f, duration);
            frontLoad = Mathf.Clamp01(frontLoad);
            float flatArea = duration;
            float rapidDuration = Mathf.Min(BleedRapidClotDuration, duration);
            float rapidArea = rapidDuration * (1f + BleedRapidClotStrength) * 0.5f;
            float tailArea = (duration - rapidDuration) *
                BleedRapidClotStrength * 0.5f;
            float decayedArea = rapidArea + tailArea;
            return Mathf.Max(0.01f, Mathf.Lerp(flatArea, decayedArea, frontLoad));
        }

        /// <summary>
        /// Strength of a bleed at a given elapsed time, on the same curve that
        /// GetBleedDecayArea integrates.
        /// </summary>
        internal static float GetBleedStrength(float elapsed, float duration,
            float frontLoad)
        {
            duration = Mathf.Max(0.01f, duration);
            if (elapsed >= duration) return 0f;
            frontLoad = Mathf.Clamp01(frontLoad);

            float rapidDuration = Mathf.Min(BleedRapidClotDuration, duration);
            float decayed;
            if (elapsed <= rapidDuration)
                decayed = Mathf.Lerp(1f, BleedRapidClotStrength,
                    Mathf.Clamp01(elapsed / rapidDuration));
            else
                decayed = Mathf.Lerp(BleedRapidClotStrength, 0f,
                    Mathf.Clamp01((elapsed - rapidDuration) /
                        Mathf.Max(0.01f, duration - rapidDuration)));
            return Mathf.Lerp(1f, decayed, frontLoad);
        }

        internal static bool IsTargetEnabled(Player player)
        {
            if (player == null) return false;
            return player.IsAI ? ApplyToAI.Value : ApplyToPlayers.Value;
        }

        internal static float GetLinkageMultiplier(EBodyPart source)
        {
            switch (source)
            {
                case EBodyPart.LeftArm:
                case EBodyPart.RightArm:
                    return ArmLinkageMultiplier.Value;
                case EBodyPart.LeftLeg:
                case EBodyPart.RightLeg:
                    return LegLinkageMultiplier.Value;
                case EBodyPart.Stomach:
                    return StomachLinkageMultiplier.Value;
                default:
                    return 1f;
            }
        }

        internal static float GetBlackedRetention(int crossedBlackedParts)
        {
            if (crossedBlackedParts <= 0) return 1f;
            if (crossedBlackedParts == 1) return OneBlackedRetention.Value;
            if (crossedBlackedParts == 2) return TwoBlackedRetention.Value;
            return ThreePlusBlackedRetention.Value;
        }

        // ---- binding helpers -------------------------------------------------

        private static ConfigEntry<bool> Toggle(ConfigFile config, string key,
            bool defaultValue, string displayName, int order,
            string description)
        {
            return config.Bind("Feature Toggles", key, defaultValue,
                Ui(description, "01 - Feature Toggles", displayName, order));
        }

        private static ConfigEntry<float> Float(ConfigFile config,
            string section, string key, float defaultValue, string category,
            string displayName, int order, float minimum, float maximum,
            string description)
        {
            return config.Bind(section, key, defaultValue,
                Ui(description, category, displayName, order,
                    new AcceptableValueRange<float>(minimum, maximum)));
        }

        private static ConfigDescription Ui(string description, string category,
            string displayName, int order, AcceptableValueBase acceptable = null)
        {
            return new ConfigDescription(description, acceptable,
                new ConfigurationManagerAttributes
                {
                    Category = category,
                    DispName = displayName,
                    Order = order
                });
        }
    }
}
