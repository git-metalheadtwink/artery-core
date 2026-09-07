namespace ArteryCore
{
    internal static class ArteryLog
    {
        private static bool IsEnabled =>
            ArteryConfig.DebugLogging != null &&
            ArteryConfig.DebugLogging.Value;

        internal static void Info(object message)
        {
            if (IsEnabled)
                Plugin.Log?.LogInfo(message);
        }

        internal static void Warning(object message)
        {
            Plugin.Log?.LogWarning(message);
        }

        internal static void Error(object message)
        {
            Plugin.Log?.LogError(message);
        }
    }
}
