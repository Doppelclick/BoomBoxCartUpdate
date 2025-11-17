using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BoomBoxCartMod.Patches
{
	public static class PlayerGrabbingTracker
	{
		// bit of a freaky solution

		// map player actor numbers to the cart they're grabbing
		public static Dictionary<int, GameObject> playerGrabbingMap = new Dictionary<int, GameObject>();

		public static bool IsLocalPlayerGrabbingCart(GameObject cart)
		{
			int localPlayerId = PhotonNetwork.LocalPlayer.ActorNumber;
			return playerGrabbingMap.ContainsKey(localPlayerId) &&
				   playerGrabbingMap[localPlayerId] == cart;
		}

		public static void SetLocalPlayerGrabbing(GameObject obj)
		{
			int localPlayerId = PhotonNetwork.LocalPlayer.ActorNumber;
			if (obj != null)
			{
				playerGrabbingMap[localPlayerId] = obj;
			}
			else if (playerGrabbingMap.ContainsKey(localPlayerId))
			{
				playerGrabbingMap.Remove(localPlayerId);
			}
		}
	}


	[HarmonyPatch(typeof(PlayerController))]
	class PlayerControllerPatch
    {
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

        [HarmonyPatch("Update")]
		[HarmonyPostfix]
		static void PatchPlayerControllerUpdate(PlayerController __instance)
		{
			if (Instance.modDisabled)
				return;

			if (__instance.physGrabObject != null)
			{
				PlayerGrabbingTracker.SetLocalPlayerGrabbing(__instance.physGrabObject.gameObject);

				// If Y key is pressed and we're grabbing a cart, try to open the boombox UI
				if (Keyboard.current != null && Keyboard.current[Instance.OpenUIKey.Value].wasPressedThisFrame)
				{
					//Logger.LogInfo($"Y key pressed while grabbing: {__instance.physGrabObject.name}");

					// check if what we're grabbing is a cart
					if (__instance.physGrabObject.GetComponent<Boombox>() != null)
					{
						// find the BoomboxController and request control
						BoomboxController controller = __instance.physGrabObject.GetComponent<BoomboxController>();
						if (controller != null)
						{
							controller.RequestBoomboxControl();
							//Logger.LogInfo($"Requested boombox control for cart: {__instance.physGrabObject.name}");
						}
					}
				}
			} 
			else
			{
				//BoomboxController controller = __instance.physGrabObject.GetComponent<BoomboxController>();
				//controller.LocalPlayerReleasedCart();

				// not grabbing anything
				PlayerGrabbingTracker.SetLocalPlayerGrabbing(null);
			}
		}
	}
}
