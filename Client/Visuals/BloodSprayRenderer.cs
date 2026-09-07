using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Systems.Effects;
using UnityEngine;

namespace ArteryCore.Visuals
{
    /// <summary>
    /// Draws the arterial blood spray every ArteryController is simulating, as
    /// one pooled stretched-billboard particle system. The material and sprite
    /// are borrowed from the blood effects of the base game so the spray
    /// matches the rest of the visual language.
    /// </summary>
    internal sealed class BloodSprayRenderer : MonoBehaviour
    {
        private const int MaximumParticles = 4096;
        private const float WorldRefreshInterval = 0.25f;

        private readonly List<ArteryController.BloodParticle> _particles =
            new List<ArteryController.BloodParticle>(1024);

        private GameWorld _world;
        private Player _localPlayer;
        private float _nextWorldRefresh;

        private GameObject _particleObject;
        private ParticleSystem _particleSystem;
        private ParticleSystemRenderer _particleRenderer;
        private Material _particleMaterial;
        private ParticleSystem.Particle[] _particleBuffer =
            new ParticleSystem.Particle[1024];

        private Texture _bloodTexture;
        private Texture2D _generatedBloodTexture;
        private Color32 _particleColor = new Color32(255, 255, 255, 235);
        private bool _usingNativeMaterial;
        private float _nextTextureLookup;
        private bool _loggedTexture;

        internal static BloodSprayRenderer Create()
        {
            GameObject host = new GameObject("ArteryCore Blood Spray");
            DontDestroyOnLoad(host);
            return host.AddComponent<BloodSprayRenderer>();
        }

        private void Update()
        {
            if (!ArteryConfig.EnableBloodEffects.Value)
            {
                Hide();
                return;
            }

            RefreshWorld();
            if (_world == null)
            {
                Hide();
                return;
            }

            EnsureRenderer();
            ResolveBloodTexture();
            CollectParticles();
            PushParticles();
        }

        private void RefreshWorld()
        {
            GameWorld next = Singleton<GameWorld>.Instance;
            if (next != _world)
            {
                _world = next;
                _localPlayer = null;
                BoneGeometry.ClearLimbBoneCache();
                WoundBallistics.ClearCaches();
                _nextWorldRefresh = 0f;
            }
            if (_world == null) return;
            if (_localPlayer != null && Time.unscaledTime < _nextWorldRefresh)
                return;
            _nextWorldRefresh = Time.unscaledTime + WorldRefreshInterval;
            _localPlayer = _world.MainPlayer;
        }

        private void CollectParticles()
        {
            _particles.Clear();
            IEnumerable<Player> players = _world.AllPlayersEverExisted;
            if (players == null) return;

            foreach (Player player in players)
            {
                if (player == null) continue;
                ArteryController artery =
                    player.GetComponent<ArteryController>();
                if (artery == null) continue;
                IList<ArteryController.BloodParticle> source =
                    artery.BloodParticles;
                for (int i = 0; i < source.Count; i++)
                    _particles.Add(source[i]);
            }
        }

        private void PushParticles()
        {
            if (_particleSystem == null || _particleRenderer == null) return;
            if (_bloodTexture == null || _particles.Count == 0)
            {
                Hide();
                return;
            }

            if (!_usingNativeMaterial &&
                _particleMaterial.mainTexture != _bloodTexture)
                _particleMaterial.mainTexture = _bloodTexture;
            if (_particleBuffer.Length < _particles.Count)
                _particleBuffer = new ParticleSystem.Particle[
                    Mathf.NextPowerOfTwo(_particles.Count)];

            float now = Time.unscaledTime;
            int count = 0;
            for (int i = 0; i < _particles.Count; i++)
            {
                ArteryController.BloodParticle source = _particles[i];
                float remaining = source.Expires - now;
                if (remaining <= 0f) continue;
                _particleBuffer[count++] = new ParticleSystem.Particle
                {
                    position = source.Position,
                    velocity = source.Velocity,
                    startLifetime = 1.5f,
                    remainingLifetime = remaining,
                    startSize = source.Size * 4.4f,
                    startColor = _particleColor,
                    randomSeed = (uint)i * 2654435761u + 1u
                };
            }

            _particleSystem.SetParticles(_particleBuffer, count);
            _particleRenderer.enabled = count > 0;
        }

        private void Hide()
        {
            if (_particleSystem != null) _particleSystem.Clear(false);
            if (_particleRenderer != null) _particleRenderer.enabled = false;
        }

        private void EnsureRenderer()
        {
            if (_particleObject != null) return;

            _particleObject = new GameObject("ArteryCore Blood Particles");
            DontDestroyOnLoad(_particleObject);
            _particleSystem = _particleObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = _particleSystem.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = MaximumParticles;
            main.startLifetime = 1.5f;
            main.startSpeed = 0f;
            main.startSize = 0.04f;
            ParticleSystem.EmissionModule emission = _particleSystem.emission;
            emission.enabled = false;

            _particleRenderer =
                _particleObject.GetComponent<ParticleSystemRenderer>();
            _particleRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            _particleRenderer.alignment = ParticleSystemRenderSpace.View;
            _particleRenderer.velocityScale = 0.085f;
            _particleRenderer.lengthScale = 0.55f;
            _particleRenderer.cameraVelocityScale = 0f;
            _particleRenderer.sortMode = ParticleSystemSortMode.Distance;
            _particleRenderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            _particleRenderer.receiveShadows = true;

            Shader shader = Shader.Find("Particles/Standard Surface") ??
                Shader.Find("Particles/Standard Unlit") ??
                Shader.Find("Unlit/Transparent");
            _particleMaterial = new Material(shader)
            {
                name = "ArteryCore Blood Particle Material",
                renderQueue = 3000
            };
            SetIfPresent(_particleMaterial, "_Color",
                new Color(0.22f, 0.012f, 0.016f, 0.94f));
            SetIfPresent(_particleMaterial, "_EmissionColor", Color.black);
            if (_particleMaterial.HasProperty("_EmissionEnabled"))
                _particleMaterial.SetFloat("_EmissionEnabled", 0f);
            if (_particleMaterial.HasProperty("_Mode"))
                _particleMaterial.SetFloat("_Mode", 2f);
            if (_particleMaterial.HasProperty("_SrcBlend"))
                _particleMaterial.SetFloat("_SrcBlend",
                    (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (_particleMaterial.HasProperty("_DstBlend"))
                _particleMaterial.SetFloat("_DstBlend",
                    (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (_particleMaterial.HasProperty("_ZWrite"))
                _particleMaterial.SetFloat("_ZWrite", 0f);
            _particleMaterial.DisableKeyword("_EMISSION");
            _particleMaterial.DisableKeyword("_EMISSIONENABLED_ON");
            _particleMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            _particleMaterial.EnableKeyword("_ALPHABLEND_ON");

            _particleRenderer.sharedMaterial = _particleMaterial;
            _particleRenderer.enabled = false;
            _particleSystem.Play(false);
        }

        private static void SetIfPresent(Material material, string property,
            Color color)
        {
            if (material.HasProperty(property))
                material.SetColor(property, color);
        }

        private void ResolveBloodTexture()
        {
            if (_bloodTexture != null ||
                Time.unscaledTime < _nextTextureLookup) return;
            _nextTextureLookup = Time.unscaledTime + 1f;

            if (TryUseNativeBloodMaterial()) return;

            TextureDecalsPainter[] painters =
                Resources.FindObjectsOfTypeAll<TextureDecalsPainter>();
            for (int i = 0; i < painters.Length; i++)
                if (painters[i] != null &&
                    painters[i]._bloodDecalTexture != null)
                {
                    _bloodTexture = BuildBloodSprite(
                        painters[i]._bloodDecalTexture);
                    break;
                }

            if (_bloodTexture == null && Singleton<Effects>.Instantiated)
            {
                Effects effects = Singleton<Effects>.Instance;
                Material material =
                    effects?.DeferredDecals?._bleedingDecal?.DecalMaterial;
                if (material != null)
                {
                    string[] properties = material.GetTexturePropertyNames();
                    for (int i = 0; i < properties.Length; i++)
                    {
                        Texture candidate = material.GetTexture(properties[i]);
                        if (!(candidate is Texture2D)) continue;
                        _bloodTexture = BuildBloodSprite(candidate);
                        if (_bloodTexture != null) break;
                    }
                }
            }

            if (_bloodTexture != null && !_loggedTexture)
            {
                _loggedTexture = true;
                ArteryLog.Info("[BloodFX] Using native blood texture: " +
                    _bloodTexture.name);
            }
        }

        private bool TryUseNativeBloodMaterial()
        {
            if (!Singleton<Effects>.Instantiated ||
                _particleRenderer == null) return false;
            Effects effects = Singleton<Effects>.Instance;
            if (effects == null || effects.EffectsArray == null) return false;

            ParticleSystemRenderer bestRenderer = null;
            ParticleSystemAdapter bestAdapter = null;
            for (int i = 0; i < effects.EffectsArray.Length; i++)
            {
                Effects.Effect effect = effects.EffectsArray[i];
                if (effect?.MaterialTypes == null || effect.Particles == null)
                    continue;

                bool bodyEffect = false;
                for (int m = 0; m < effect.MaterialTypes.Length; m++)
                    if (effect.MaterialTypes[m] ==
                        EFT.Ballistics.MaterialType.Body)
                    {
                        bodyEffect = true;
                        break;
                    }
                if (!bodyEffect) continue;

                for (int p = 0; p < effect.Particles.Length; p++)
                {
                    ParticleSystemAdapter adapter =
                        effect.Particles[p].Particle as ParticleSystemAdapter;
                    if (adapter == null ||
                        adapter.ParticleSystemObject == null) continue;
                    ParticleSystemRenderer source = adapter
                        .ParticleSystemObject
                        .GetComponent<ParticleSystemRenderer>();
                    if (source == null || source.sharedMaterial == null)
                        continue;

                    if (bestRenderer == null)
                    {
                        bestRenderer = source;
                        bestAdapter = adapter;
                    }
                    // Prefer an explicitly blood-named system when there is one.
                    string combined = (source.name + " " +
                        source.sharedMaterial.name + " " +
                        adapter.ParticleSystemObject.name).ToLowerInvariant();
                    if (!combined.Contains("blood")) continue;
                    bestRenderer = source;
                    bestAdapter = adapter;
                    p = effect.Particles.Length;
                }
                if (bestRenderer != null) break;
            }

            if (bestRenderer == null || bestRenderer.sharedMaterial == null)
                return false;

            if (_particleMaterial != null) Destroy(_particleMaterial);
            _particleMaterial = new Material(bestRenderer.sharedMaterial)
            {
                name = "ArteryCore Native Blood Particle Material"
            };
            _particleRenderer.sharedMaterial = _particleMaterial;
            _bloodTexture = _particleMaterial.mainTexture != null
                ? _particleMaterial.mainTexture : Texture2D.whiteTexture;
            _particleColor = bestAdapter != null
                ? bestAdapter.Color : new Color32(255, 255, 255, 235);
            _particleColor.a = 255;
            ForceOpaque(_particleMaterial, "_Color");
            ForceOpaque(_particleMaterial, "_TintColor");
            ForceOpaque(_particleMaterial, "_BaseColor");
            _usingNativeMaterial = true;
            _loggedTexture = true;
            ArteryLog.Info(
                "[BloodFX] Cloned native body-impact particle material: " +
                bestRenderer.sharedMaterial.name);
            return true;
        }

        private static void ForceOpaque(Material material, string property)
        {
            if (material == null || !material.HasProperty(property)) return;
            Color color = material.GetColor(property);
            color.a = 1f;
            material.SetColor(property, color);
        }

        /// <summary>
        /// Converts a decal mask into an alpha sprite usable by a particle
        /// system, cropping away the empty border.
        /// </summary>
        private Texture2D BuildBloodSprite(Texture source)
        {
            if (source == null) return null;
            RenderTexture temporary = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                int width = Mathf.Clamp(source.width, 8, 1024);
                int height = Mathf.Clamp(source.height, 8, 1024);
                temporary = RenderTexture.GetTemporary(width, height, 0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;

                Texture2D readable = new Texture2D(width, height,
                    TextureFormat.RGBA32, false, false);
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0,
                    false);
                readable.Apply(false, false);
                Color32[] pixels = readable.GetPixels32();

                byte minAlpha = 255, maxAlpha = 0, maxIntensity = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 pixel = pixels[i];
                    if (pixel.a < minAlpha) minAlpha = pixel.a;
                    if (pixel.a > maxAlpha) maxAlpha = pixel.a;
                    byte intensity = (byte)Mathf.Max(pixel.r,
                        Mathf.Max(pixel.g, pixel.b));
                    if (intensity > maxIntensity) maxIntensity = intensity;
                }
                bool hasUsefulAlpha = minAlpha < 48 && maxAlpha - minAlpha > 96;
                float intensityScale =
                    maxIntensity > 0 ? 1f / maxIntensity : 0f;

                int minX = width, minY = height, maxX = -1, maxY = -1;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;
                        Color32 pixel = pixels[index];
                        float mask = hasUsefulAlpha
                            ? pixel.a / 255f
                            : Mathf.Max(pixel.r,
                                Mathf.Max(pixel.g, pixel.b)) * intensityScale;
                        mask = Mathf.SmoothStep(0f, 1f,
                            Mathf.InverseLerp(0.06f, 0.72f, mask));
                        byte alpha = (byte)Mathf.RoundToInt(mask * 235f);
                        pixels[index] = new Color32(255, 255, 255, alpha);
                        if (alpha <= 10) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                Destroy(readable);
                if (maxX < minX || maxY < minY) return null;

                int croppedWidth = maxX - minX + 1;
                int croppedHeight = maxY - minY + 1;
                Color32[] cropped = new Color32[croppedWidth * croppedHeight];
                for (int y = 0; y < croppedHeight; y++)
                    for (int x = 0; x < croppedWidth; x++)
                        cropped[y * croppedWidth + x] =
                            pixels[(y + minY) * width + x + minX];

                _generatedBloodTexture = new Texture2D(croppedWidth,
                    croppedHeight, TextureFormat.RGBA32, false, false)
                {
                    name = "ArteryCore Blood Sprite",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                _generatedBloodTexture.SetPixels32(cropped);
                _generatedBloodTexture.Apply(false, true);
                return _generatedBloodTexture;
            }
            catch (System.Exception exception)
            {
                ArteryLog.Warning(
                    "[BloodFX] Could not convert the native decal mask: " +
                    exception.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private void OnDestroy()
        {
            if (_particleObject != null) Destroy(_particleObject);
            if (_particleMaterial != null) Destroy(_particleMaterial);
            if (_generatedBloodTexture != null)
                Destroy(_generatedBloodTexture);
        }
    }
}
