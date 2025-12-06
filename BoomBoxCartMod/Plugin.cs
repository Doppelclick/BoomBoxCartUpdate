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
		public const string modVersion = "1.3.1";

		private readonly Harmony harmony = new Harmony(modGUID);
        public BaseListener baseListener = null;

		internal static BoomBoxCartMod instance;
		internal ManualLogSource logger;

		private bool modDisabledValue = false;
		public PersistentData data;


		public bool modDisabled
		{
			get => modDisabledValue;
			set {
                if (modDisabledValue != value)
                {
                    logger.LogInfo("Mod " + (value ? "Disabled" : "Enabled"));
                }

                modDisabledValue = value;

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

			Task.Run(() => YoutubeDL.InitializeAsync().Wait());

			OpenUIKey = Config.Bind("General", "OpenUIKey", Key.Y, "Key to open the Boombox UI when grabbing a cart.");
            GlobalMuteKey = Config.Bind("General", "GlobalMuteKey", Key.M, "Key to mute all playback.");
            MasterClientDismissQueue = Config.Bind("General", "MasterClientDismissQueue", true, "Allow only the master client to dismiss the queue.");
            UseTimeStampOnce = Config.Bind("General", "UseTimeStampOnce", false, "Only use the timestamp provided with a Url the first time the song is played.");
            RestoreBoomboxes = Config.Bind("General", "RestoreBoomboxes", true, "Restore BoomBoxes and their Queues when you load back into a level.");
		    AutoResume = Config.Bind("General", "AutoResume", false, "Automatically resume playback when entering a new lobby.");
			VisualizerBehaviourPaused = Config.Bind("General", "VisualizerPaused", VisualizerPaused.Hide, "Visualizer behaviour when music is paused.");

            logger.LogInfo("BoomBoxCartMod initialization finished!");
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
