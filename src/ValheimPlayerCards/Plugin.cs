using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPlayerCards
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "Manke.ValheimPlayerCards";
        public const string Name = "ValheimPlayerCards";
        public const string Version = "0.2.1";

        internal const string DefaultStats =
            "Deaths,EnemyKills,BossKills,PlayerKills,Builds,Crafts,TreeChops,MineHits,FishCaught,CreatureTamed,FoodEaten,PortalsUsed,DistanceTraveled,DistanceSail,ArrowsShot,Jumps";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        /// <summary>Dedicated servers run without graphics and never render.</summary>
        internal static readonly bool IsHeadless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        // Server
        internal static ConfigEntry<bool> ServerEnabled;
        internal static ConfigEntry<string> DataDirectory;
        internal static ConfigEntry<int> MaxUploadKB;
        internal static ConfigEntry<int> ServerMinIntervalSeconds;
        internal static ConfigEntry<string> HiddenPlayers;

        // Web
        internal static ConfigEntry<bool> WebEnabled;
        internal static ConfigEntry<string> WebHost;
        internal static ConfigEntry<int> WebPort;
        internal static ConfigEntry<string> AllowOrigin;
        internal static ConfigEntry<string> PageLanguage;
        internal static ConfigEntry<string> PageTitle;
        internal static ConfigEntry<string> CustomPageDirectory;

        // Card
        internal static ConfigEntry<bool> ShowPlayTime;
        internal static ConfigEntry<bool> ShowEquipment;
        internal static ConfigEntry<bool> ShowItemIcons;
        internal static ConfigEntry<bool> ShowSkills;
        internal static ConfigEntry<bool> ShowStats;
        internal static ConfigEntry<string> StatList;

        // Client
        internal static ConfigEntry<bool> ClientEnabled;
        internal static ConfigEntry<bool> ShareSkills;
        internal static ConfigEntry<bool> ShareStats;
        internal static ConfigEntry<int> MinIntervalSeconds;
        internal static ConfigEntry<int> Width;
        internal static ConfigEntry<int> Height;
        internal static ConfigEntry<int> Supersampling;
        internal static ConfigEntry<int> TargetKB;
        internal static ConfigEntry<bool> SaveLocalCopy;

        // Look
        internal static ConfigEntry<float> Yaw;
        internal static ConfigEntry<float> Pitch;
        internal static ConfigEntry<float> Ambient;
        internal static ConfigEntry<float> KeyLight;
        internal static ConfigEntry<float> RimLight;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            const string server = "1 - Server";
            ServerEnabled = Config.Bind(server, "Enabled", true,
                "Accept player cards (portrait, equipment, skills, stats) from players. Only used when this game instance is the server.");
            DataDirectory = Config.Bind(server, "DataDirectory", "",
                "Where cards are stored. Empty = BepInEx/config/ValheimPlayerCards/<world name>. A relative path starts from the Valheim folder.");
            MaxUploadKB = Config.Bind(server, "MaxUploadKB", 700,
                new ConfigDescription("Largest upload accepted (portrait, item icons and data).", new AcceptableValueRange<int>(64, Protocol.HardMaxBytes / 1024)));
            ServerMinIntervalSeconds = Config.Bind(server, "MinIntervalSeconds", 20,
                new ConfigDescription("Minimum time between two uploads from the same player.", new AcceptableValueRange<int>(5, 3600)));
            HiddenPlayers = Config.Bind(server, "HiddenPlayers", "",
                "Comma-separated player names that never appear on the page or in the API.");

            const string web = "2 - Web";
            WebEnabled = Config.Bind(web, "Enabled", true,
                "Serve the player cards page and the JSON API. Only on the server.");
            WebHost = Config.Bind(web, "Host", "*",
                "Address to listen on. * = all addresses, 127.0.0.1 = only this machine (for a reverse proxy on the same host).");
            WebPort = Config.Bind(web, "Port", 3010,
                new ConfigDescription("TCP port of the web page and API.", new AcceptableValueRange<int>(1, 65535)));
            AllowOrigin = Config.Bind(web, "AllowOrigin", "*",
                "Value of the Access-Control-Allow-Origin header, so other websites can read the API. * = any site, empty = only this page.");
            PageLanguage = Config.Bind(web, "Language", "auto",
                "Language of the page: auto (from the visitor's browser) or a language code: en, de, fr, es, pt, ru, pl, it.");
            PageTitle = Config.Bind(web, "Title", "",
                "Title shown on the page. Empty = the server name.");
            CustomPageDirectory = Config.Bind(web, "CustomPageDirectory", "",
                "Folder with your own index.html, app.js, lang.js or style.css; files found there replace the built-in ones.");

            const string card = "3 - Card";
            ShowPlayTime = Config.Bind(card, "ShowPlayTime", true,
                "Online now, time played, sessions, first and last seen. Tracked by the server.");
            ShowEquipment = Config.Bind(card, "ShowEquipment", true,
                "List of the equipped items.");
            ShowItemIcons = Config.Bind(card, "ShowItemIcons", true,
                "Game icons next to the equipped items.");
            ShowSkills = Config.Bind(card, "ShowSkills", true,
                "Skill levels.");
            ShowStats = Config.Bind(card, "ShowStats", true,
                "Character statistics from the game (deaths, kills, builds...). They count all worlds the character has played.");
            StatList = Config.Bind(card, "StatList", DefaultStats,
                "Statistics shown, in this order. Names are Valheim's PlayerStatType values.");

            const string client = "4 - Client";
            ClientEnabled = Config.Bind(client, "Enabled", true,
                "Render your character and send your card to servers that have ValheimPlayerCards.");
            ShareSkills = Config.Bind(client, "ShareSkills", true,
                "Send your skill levels with the card.");
            ShareStats = Config.Bind(client, "ShareStats", true,
                "Send your character statistics with the card.");
            MinIntervalSeconds = Config.Bind(client, "MinIntervalSeconds", 30,
                new ConfigDescription("Minimum time between two cards. The server can ask for a longer wait.", new AcceptableValueRange<int>(10, 3600)));
            Width = Config.Bind(client, "Width", 512,
                new ConfigDescription("Portrait width in pixels.", new AcceptableValueRange<int>(128, 1024)));
            Height = Config.Bind(client, "Height", 768,
                new ConfigDescription("Portrait height in pixels.", new AcceptableValueRange<int>(128, 1536)));
            Supersampling = Config.Bind(client, "Supersampling", 2,
                new ConfigDescription("Render at this many times the size and scale down, for smoother edges.", new AcceptableValueRange<int>(1, 3)));
            TargetKB = Config.Bind(client, "TargetKB", 300,
                new ConfigDescription("If the portrait PNG is larger, it is scaled down until it fits.", new AcceptableValueRange<int>(32, 1024)));
            SaveLocalCopy = Config.Bind(client, "SaveLocalCopy", true,
                "Also save the last portrait as BepInEx/ValheimPlayerCards/last-portrait.png on your PC.");

            const string look = "5 - Look";
            Yaw = Config.Bind(look, "Yaw", -5f,
                new ConfigDescription("Character rotation in degrees. The idle stance already turns the body about 35 degrees, so around -5 gives a 3/4 view.", new AcceptableValueRange<float>(-180f, 180f)));
            Pitch = Config.Bind(look, "Pitch", 0f,
                new ConfigDescription("Forward tilt in degrees.", new AcceptableValueRange<float>(-30f, 30f)));
            Ambient = Config.Bind(look, "Ambient", 0.5f,
                new ConfigDescription("Ambient light (0 = black, 1 = white).", new AcceptableValueRange<float>(0f, 1.5f)));
            KeyLight = Config.Bind(look, "KeyLight", 0.6f,
                new ConfigDescription("Light from the upper front side.", new AcceptableValueRange<float>(0f, 3f)));
            RimLight = Config.Bind(look, "RimLight", 0.5f,
                new ConfigDescription("Light from behind that outlines the character.", new AcceptableValueRange<float>(0f, 3f)));

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            if (!IsHeadless)
                CommandManager.Instance.AddConsoleCommand(new CardCommand());

            Log.LogInfo($"{Name} {Version} loaded.");
        }

        private void Update()
        {
            ClientSide.Tick();
            PlayerStore.Tick();
        }

        private void OnDestroy()
        {
            ServerSide.Stop();
            _harmony?.UnpatchSelf();
        }
    }
}
