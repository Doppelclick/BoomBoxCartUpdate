using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BoomBoxCartMod.Patches
{
	[HarmonyPatch(typeof(PhysGrabCart))]
	class PhysGrabCartPatch
    {
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		[HarmonyPatch("Start")]
		[HarmonyPostfix]
		static void PatchPhysGrabCartStart(PhysGrabCart __instance)
		{
            if (Instance.modDisabled)
                return;

            //Logger.LogInfo($"PhysGrabCart Start: {__instance.name}");

            // if this cart is active while in the shop, don't add boombox to them
            if (RunManager.instance.levelCurrent == RunManager.instance.levelShop)
			{
				return;
			}

			if (__instance.GetComponent<Boombox>() == null)
			{
				__instance.gameObject.AddComponent<Boombox>();
				//Logger.LogInfo($"Boombox component added to {__instance.name}");
			}

			if (__instance.GetComponent<BoomboxController>() == null)
			{
				__instance.gameObject.AddComponent<BoomboxController>();
				//Logger.LogInfo($"BoomboxController component added to {__instance.name}");
			}
		}
	}
}
