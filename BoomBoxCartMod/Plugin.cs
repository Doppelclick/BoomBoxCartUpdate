using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BoomBoxCartMod.Util;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine;
using Photon.Pun;

namespace BoomBoxCartMod
{
	[BepInPlugin(modGUID, modName, modVersion)]
	public class BoomBoxCartMod : BaseUnityPlugin
	{
		public const string modGUID = "Doppelclick.BoomboxCartUpgrade";
		public const string modName = "BoomboxCartUpgrade";
		public const string modVersion = "1.3.2";

		private readonly Harmony harmony = new Harmony(modGUID);
        public BaseListener baseListener = null;

		internal static BoomBoxCartMod instance;
		internal ManualLogSource logger;

        public bool initialized { get; private set; } = false;
		public PersistentData data;


		public bool modDisabled
		{
			get;
			set {
                if (field != value)
                {
                    logger.LogInfo("Mod " + (value ? "Disabled" : "Enabled"));
                }

                field = value;

                if (value && data != null)
                {
                    foreach (Boombox boombox in data.GetAllBoomboxes())
                    {
                        PhotonView? view = boombox.photonView;
                        Destroy(boombox);
                        view?.RefreshRpcMonoBehaviourCache(); // TODO: This does not actually work
                    }
                    data.GetAllBoomboxes().Clear();
                    data.GetBoomboxData().Clear();
                }
            }
		}



		public ConfigEntry<Key> OpenUIKey { get; private set; }
        public ConfigEntry<Key> GlobalMuteKey { get; private set; }

        public ConfigEntry<bool> MasterClientDismissQueue { get; private set; }
        public ConfigEntry<bool> UseTimeStampOnce { get; private set; }
        public ConfigEntry<bool> RestoreBoomboxes { get; private set; }
        public ConfigEntry<bool> AutoResume { get; private set; }

        public ConfigEntry<float> UnderglowBeatSpeed { get; private set; }
        public ConfigEntry<float> UnderglowBassBias { get; private set; }

        private bool resourcesReinstalling = false;
        public ConfigEntry<bool> ReinstallResources { get; private set;}


        public ConfigEntry<VisualizerPaused> VisualizerBehaviourPaused { get; private set; }

        private void Awake()
		{
			if (instance == null)
			{
				instance = this;
			}
			data = new PersistentData();
            logger = BepInEx.Logging.Logger.CreateLogSource(modGUID);
			logger.LogInfo("BoomBoxCartMod loaded!");

			harmony.PatchAll();

            OpenUIKey = Config.Bind("Binds", "OpenUIKey", Key.Y, "Key to open the Boombox UI when grabbing a cart.");
            GlobalMuteKey = Config.Bind("Binds", "GlobalMuteKey", Key.M, "Key to mute all playback.");
            
            MasterClientDismissQueue = Config.Bind("Queue", "MasterClientDismissQueue", true, "Allow only the master client to dismiss the queue.");
            UseTimeStampOnce = Config.Bind("Queue", "UseTimeStampOnce", false, "Only use the timestamp provided with a Url the first time the song is played.");
            RestoreBoomboxes = Config.Bind("Queue", "RestoreBoomboxes", true, "Restore BoomBoxes and their Queues when you load back into a level.");
		    AutoResume = Config.Bind("Queue", "AutoResume", false, "Automatically resume playback when entering a new lobby.");
            
            UnderglowBeatSpeed = Config.Bind("Visual", "UnderglowBeatSpeed", 1.2f, new ConfigDescription("Scales how much detected beat energy speeds up the RGB underglow cycle. 0 = disabled, 6 = more aggressive.", new AcceptableValueRange<float>(0f, 6f)));
            UnderglowBassBias = Config.Bind("Visual", "UnderglowBassBias", 0.8f, new ConfigDescription("Blends between broad beat response and isolated low-band response. 0 = broad response, 1 = strongest bass isolation.", new AcceptableValueRange<float>(0f, 1f)));
			VisualizerBehaviourPaused = Config.Bind("Visual", "VisualizerOnPaused", VisualizerPaused.Hide, "Visualizer behaviour when music is paused.");
           	
            ReinstallResources = Config.Bind("Debug", "ReinstallResources", false, "Reinstall resources on startup.");
            ReinstallResources.SettingChanged += (s, _) => { // Can rarely cause game freezes, when toggled ingame
                if (!initialized)
                    return;
                
                var entry = (ConfigEntry<bool>)s;
                if (resourcesReinstalling) {
                    if (!entry.Value) {
                        entry.Value = true; // Prevent unsetting the value while reinstalling
                    }
                    return;
                }

                if (YoutubeDL.IsUpdatingResources)
                {
                    logger.LogInfo("Already updating resources...");
                    entry.Value = false; // Prevent changing the value while updating
                    return;
                }

                if (entry.Value)
                {
                    logger.LogInfo("Reinstalling resources...");
                    resourcesReinstalling = true;
                }

                Task.Run(async () => { 
                    await YoutubeDL.Reinstall(); 
                    logger.LogInfo("Finished reinstalling resources.");
                    ReinstallResources.Value = false;
                    resourcesReinstalling = false;
                });
            };

            logger.LogInfo("BoomBoxCartMod initialization finished!");
            initialized = true;
        }

        void Start()
        {
            if (ReinstallResources.Value) {
				ReinstallResources.Value = true;
			}
            else
            {
                _ = YoutubeDL.InitializeAsync();
            }
        }

        private void OnDestroy()
		{
			YoutubeDL.CleanUp();
        }

        public enum VisualizerPaused
        {
            Show,
            PausePosition,
            Hide
        }
    }
}
