using System;
using BepInEx.Configuration;

namespace ArteryCore
{
    internal sealed class ConfigurationManagerAttributes
    {
        public Action<ConfigEntryBase> CustomDrawer;
        public string Category;
        public string DispName;
        public int Order;
        public bool HideDefaultButton;
        public bool HideSettingName;
    }
}
