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
						Task.Delay(200);

						float startTime = Time.time;
						while (boombox.photonView != null)
						{
							if (Instance.baseListener == null)
							{
                                Task.Delay(200);
                                continue;
							}

							var modUsers = Instance.baseListener.GetAllModUsers();

							if (modUsers.Count < Instance.baseListener.lastUserAmount && Time.time - startTime < 10) // Wait until ModFeedbackCheck RPC is returned
                            {
                                Task.Delay(200);
								continue;
                            }
							else
							{
								startTime = Time.time;
							}

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
								break;
							}

                            if (Time.time - startTime > 3) // Wait max 3 seconds
							{
								break;
                            }
                            Task.Delay(200);
                        }

                        boombox.SyncInitializeWithOthers();
                    });
				}
                Logger.LogInfo($"Boombox component added to {__instance.name}");
            }

            if (__instance.GetComponent<BoomboxController>() == null)
			{
				__instance.gameObject.AddComponent<BoomboxController>();
				//Logger.LogInfo($"BoomboxController component added to {__instance.name}");
			}
		}
	}
}
