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

namespace BoomBoxCartMod
{
	[BepInPlugin(modGUID, modName, modVersion)]
	public class BoomBoxCartMod : BaseUnityPlugin
	{
		private const string modGUID = "ColtG5.BoomboxCart";
		private const string modName = "BoomboxCart";
		private const string modVersion = "1.2.2";

		private readonly Harmony harmony = new Harmony(modGUID);

		internal static BoomBoxCartMod instance;
		internal ManualLogSource logger;

		public ConfigEntry<Key> OpenUIKey { get; private set; }

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
		}

		private void OnDestroy()
		{
			YoutubeDL.CleanUp();
        }

    }
}
