using BepInEx.Logging;
using BoomBoxCartMod.Util;
using HarmonyLib;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

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
				Boombox boombox = __instance.gameObject.AddComponent<Boombox>();
                Instance.data.InitializeBoomboxData(boombox);
				if (PhotonNetwork.IsMasterClient)
				{
					Task.Run(async () =>
					{
						float startTime = Time.time;
						while (boombox.photonView != null)
						{
							var modUsers = Instance.baseListener.GetAllModUsers();
							int count = 0;

							foreach (var player in PhotonNetwork.PlayerList) {
                                if (modUsers.Contains(player.ActorNumber) &&
									PersistentData.GetBoomboxViewStatus(player, boombox.photonView.ViewID)
								) {
									count++;
								}
							}

							if (count >= modUsers.Count)
							{
								boombox.SyncInitializeWithOthers();
								break;
							}

                            if (Time.time - startTime > 5) // Wait max 5 seconds
							{
								break;
                            }
                            Task.Delay(200);
                        }
					});
				}
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
