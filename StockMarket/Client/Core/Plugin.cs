using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TarkFX.StockMarket.Client.Core;

namespace TarkFX.StockMarket.Client
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.hiddencirno.tarkfx.stockmarket";
        public const string Name = "TarkFX Stock Market";
        public const string Version = "0.1.0";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            ClientConfig.Init(Config);
            _harmony = new Harmony(Guid);
            _harmony.PatchAll();
            Log.LogInfo($"{Name} {Version} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            Instance = null;
        }
    }
}
