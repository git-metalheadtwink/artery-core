using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using ArteryCore.Patches.Bleeding;
using ArteryCore.Patches.Presentation;

namespace ArteryCore.Patches.Shot
{
    /// <summary>
    /// Player.ApplyShot, Player.ApplyDamageInfo and Player.OnDead are all
    /// <c>virtual</c>. Harmony patching a base virtual method does NOT
    /// intercept a subclass that overrides it, and co-op frameworks such as
    /// Fika replace every player and every bot with their own Player
    /// subclasses whose overrides reimplement the method without ever calling
    /// base. Patching only EFT.Player therefore silently does nothing in those
    /// setups.
    ///
    /// This discovers every loaded subclass of Player that declares its own
    /// override and patches each one, so the shot pipeline runs regardless of
    /// who owns the Player type.
    /// </summary>
    internal static class VirtualPlayerPatches
    {
        private static readonly Type[] ApplyShotParameters =
        {
            typeof(DamageInfo), typeof(EBodyPart),
            typeof(EBodyPartColliderType), typeof(EArmorPlateCollider),
            typeof(ShotId)
        };

        private static readonly Type[] ApplyDamageInfoParameters =
        {
            typeof(DamageInfo), typeof(EBodyPart),
            typeof(EBodyPartColliderType), typeof(float)
        };

        private static readonly Type[] OnDeadParameters =
        {
            typeof(EDamageType)
        };

        internal static void ApplyAll(Harmony harmony)
        {
            PatchAll(harmony, "ApplyShot", ApplyShotParameters,
                prefix: AccessTools.Method(typeof(ArteryShotPatch),
                    nameof(ArteryShotPatch.ClassifyShot)),
                postfix: AccessTools.Method(typeof(ArteryShotPatch),
                    nameof(ArteryShotPatch.ApplyShotResults)),
                finalizer: AccessTools.Method(typeof(ArteryShotPatch),
                    nameof(ArteryShotPatch.ResetShotState)));

            PatchAll(harmony, "ApplyDamageInfo", ApplyDamageInfoParameters,
                prefix: AccessTools.Method(typeof(BleedKillAttribution),
                    nameof(BleedKillAttribution.CaptureHit)),
                postfix: null, finalizer: null);

            PatchAll(harmony, "OnDead", OnDeadParameters,
                prefix: AccessTools.Method(typeof(ArteryDeathVoice),
                    nameof(ArteryDeathVoice.ChooseDeathPhrase)),
                postfix: AccessTools.Method(typeof(ArteryDeathVoice),
                    nameof(ArteryDeathVoice.PlayDeathPhrase)),
                finalizer: AccessTools.Method(typeof(ArteryDeathVoice),
                    nameof(ArteryDeathVoice.ResetState)));
        }

        private static void PatchAll(Harmony harmony, string methodName,
            Type[] parameters, MethodInfo prefix, MethodInfo postfix,
            MethodInfo finalizer)
        {
            List<MethodInfo> targets = FindDeclaredImplementations(methodName,
                parameters);
            int patched = 0;
            foreach (MethodInfo target in targets)
            {
                try
                {
                    harmony.Patch(target,
                        prefix == null ? null : new HarmonyMethod(prefix),
                        postfix == null ? null : new HarmonyMethod(postfix),
                        transpiler: null,
                        finalizer: finalizer == null
                            ? null : new HarmonyMethod(finalizer),
                        ilmanipulator: null);
                    patched++;
                }
                catch (Exception exception)
                {
                    ArteryLog.Warning(
                        $"Could not patch {target.DeclaringType?.FullName}::" +
                        $"{methodName}: {exception.Message}");
                }
            }

            string names = string.Join(", ", targets.ConvertAll(
                target => target.DeclaringType?.Name ?? "?").ToArray());
            Plugin.Log.LogInfo(
                $"Patched {methodName} on {patched}/{targets.Count} " +
                $"implementation(s): {names}");
        }

        /// <summary>
        /// The base implementation plus every loaded subclass that declares its
        /// own override of the same signature.
        /// </summary>
        private static List<MethodInfo> FindDeclaredImplementations(
            string methodName, Type[] parameters)
        {
            List<MethodInfo> targets = new List<MethodInfo>();

            MethodInfo baseMethod = AccessTools.Method(typeof(Player),
                methodName, parameters);
            if (baseMethod != null) targets.Add(baseMethod);

            const BindingFlags flags = BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.DeclaredOnly;

            foreach (Assembly assembly in
                AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch (Exception)
                {
                    continue;
                }
                if (types == null) continue;

                foreach (Type type in types)
                {
                    if (type == null || type == typeof(Player) ||
                        type.IsInterface || type.IsAbstract ||
                        !typeof(Player).IsAssignableFrom(type)) continue;

                    MethodInfo declared;
                    try
                    {
                        declared = type.GetMethod(methodName, flags, null,
                            parameters, null);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (declared == null || declared.IsAbstract ||
                        targets.Contains(declared)) continue;
                    targets.Add(declared);
                }
            }
            return targets;
        }
    }
}
