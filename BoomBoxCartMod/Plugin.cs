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

namespace BoomBoxCartMod
{
	[BepInPlugin(modGUID, modName, modVersion)]
	public class BoomBoxCartMod : BaseUnityPlugin
	{
		public const string modGUID = "Doppelclick.BoomboxCartUpgrade";
		public const string modName = "BoomboxCartUpgrade";
		public const string modVersion = "1.2.4";

		private readonly Harmony harmony = new Harmony(modGUID);
        public BaseListener baseListener = null;

		internal static BoomBoxCartMod instance;
		internal ManualLogSource logger;

		public bool modDisabled = false;

		public ConfigEntry<Key> OpenUIKey { get; private set; }
        public ConfigEntry<Key> GlobalMuteKey { get; private set; }
        public ConfigEntry<bool> MasterClientDismissQueue { get; private set; }
        public ConfigEntry<bool> UseTimeStampOnce { get; private set; }

        private void Awake()
		{
			if (instance == null)
			{
				instance = this;
			}
			logger = BepInEx.Logging.Logger.CreateLogSource(modGUID);
			logger.LogInfo("BoomBoxCartMod loaded!");

			Task.Run(() => YoutubeDL.InitializeAsync().Wait());

			harmony.PatchAll();

			OpenUIKey = Config.Bind("General", "OpenUIKey", Key.Y, "Key to open the Boombox UI when grabbing a cart.");
            GlobalMuteKey = Config.Bind("General", "GlobalMuteKey", Key.M, "Key to mute all playback."); // TODO: Possibly make default value Key.None
            MasterClientDismissQueue = Config.Bind("General", "MasterClientDismissQueue", true, "Allow only the master client to dismiss the queue.");
            UseTimeStampOnce = Config.Bind("General", "UseTimeStampOnce", false, "Only use the timestamp provided with a Url the first time it is played.");
            
			logger.LogInfo("BoomBoxCartMod initialization finished!");
        }

        private void OnDestroy()
		{
			YoutubeDL.CleanUp();
        }

    }
}
