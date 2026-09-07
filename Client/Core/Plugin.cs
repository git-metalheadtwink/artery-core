using System;
using System.Collections.Generic;
using ArteryCore.Patches.Bleeding;
using ArteryCore.Patches.Shot;
using ArteryCore.Visuals;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace ArteryCore
{
    /// <summary>
    /// ArteryCore adds arterial bleeding to the major superficial arteries and
    /// deterministic fractures to the bones, plus bruising, impact shock and
    /// blood presentation. Every other hit is left exactly as vanilla resolved
    /// it: no bullet damage is ever rescaled.
    /// </summary>
    // Soft dependency: Fika replaces every Player with its own subclasses, and
    // VirtualPlayerPatches has to see those types to patch their overrides. The
    // dependency forces Fika to load first when it is installed.
    [BepInDependency(FikaGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.arterycore";
        public const string Name = "ArteryCore";
        public const string Version = "1.3.2";
        private const string FikaGuid = "com.fika.core";

        internal static ManualLogSource Log { get; private set; }

        private PatchManager _patchManager;
        private Harmony _virtualHarmony;
        private BloodSprayRenderer _bloodRenderer;
        private GameWorld _world;

        private void Awake()
        {
            Log = Logger;
            BindDefaultPresetButton();
            ArteryConfig.Initialize(Config);

            _patchManager = new PatchManager(this, autoPatch: true);
            _patchManager.EnablePatches();

            // Patched manually rather than through PatchManager: these targets
            // are virtual, so every declared override has to be hooked, not
            // just the base method.
            _virtualHarmony = new Harmony(Guid + ".virtual");
            VirtualPlayerPatches.ApplyAll(_virtualHarmony);

            _bloodRenderer = BloodSprayRenderer.Create();
            GameWorld.OnDispose += OnWorldDisposed;

            Logger.LogInfo(Name + " " + Version + " loaded");
        }

        private async void Start()
        {
            // ENVIRONMENT_HIT_MASK is needed before blood particles can splat
            // against the world.
            try
            {
                await EFTHardSettings.Load();
            }
            catch (Exception exception)
            {
                ArteryLog.Error(exception);
            }
        }

        private void Update()
        {
            GameWorld next = Singleton<GameWorld>.Instance;
            if (next == _world) return;
            _world = next;
            if (_world == null) OnWorldDisposed();
        }

        private void OnWorldDisposed()
        {
            BoneGeometry.ClearLimbBoneCache();
            WoundBallistics.ClearCaches();
            BleedKillAttribution.Clear();
        }

        // ---- default preset --------------------------------------------------

        private void BindDefaultPresetButton()
        {
            ConfigurationManagerAttributes attributes =
                new ConfigurationManagerAttributes
                {
                    Category = "00 - Default Preset",
                    DispName = "Restore Recommended Defaults",
                    Order = 1000,
                    HideDefaultButton = true,
                    HideSettingName = true,
                    CustomDrawer = ignored => DrawDefaultPresetButton()
                };
            Config.Bind("Default Preset", "Restore Recommended Defaults", false,
                new ConfigDescription(
                    "Restore every ArteryCore setting to the curated defaults.",
                    null, attributes));
        }

        private void DrawDefaultPresetButton()
        {
            if (GUILayout.Button("Restore Recommended Defaults",
                GUILayout.ExpandWidth(true)))
                RestoreRecommendedDefaults();
        }

        private void RestoreRecommendedDefaults()
        {
            bool previousSaveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            int restored = 0;
            try
            {
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> setting
                    in Config)
                {
                    ConfigEntryBase entry = setting.Value;
                    if (entry == null ||
                        Equals(entry.BoxedValue, entry.DefaultValue)) continue;
                    entry.BoxedValue = entry.DefaultValue;
                    restored++;
                }
                Config.Save();
            }
            finally
            {
                Config.SaveOnConfigSet = previousSaveOnSet;
            }
            Logger.LogInfo("[DefaultPreset] Restored " + restored +
                " ArteryCore settings to defaults");
        }

        private void OnDestroy()
        {
            GameWorld.OnDispose -= OnWorldDisposed;
            HitPressureVignette.RemoveOverlay();
            if (_bloodRenderer != null) Destroy(_bloodRenderer.gameObject);
            _patchManager?.DisablePatches();
            _virtualHarmony?.UnpatchSelf();
        }
    }
}
