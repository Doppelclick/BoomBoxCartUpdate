using BepInEx.Logging;
using BoomBoxCartMod.Util;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace BoomBoxCartMod.Patches
{
    [HarmonyPatch(typeof(LevelGenerator))]
    class LevelGeneratorPatch
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
        private static ManualLogSource Logger => Instance.logger;

        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        static void StartPatch(LevelGenerator __instance)
        {
            if (__instance.gameObject == null)
                return;

            if (Instance.baseListener == null)
            {
                Instance.baseListener = __instance.gameObject.AddComponent<BaseListener>();
            }

            if (Instance.baseListener == null || !PhotonNetwork.IsConnected || Instance.modDisabled)
                return;

            Logger.LogInfo("Level started, checking if other players are using the mod.");

            Instance.baseListener.lastUserAmount = Instance.baseListener.GetAllModUsers().Count;
            Instance.baseListener.GetAllModUsers().Clear(); // No need to send UpdateModList RPC because every client does this

            if (PhotonNetwork.IsMasterClient && !Instance.modDisabled)
            {
                Instance.baseListener.photonView?.RPC(
                    "ModFeedbackCheck",
                    RpcTarget.Others,
                    BoomBoxCartMod.modVersion,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                Instance.baseListener.AddModUser(PhotonNetwork.LocalPlayer.ActorNumber);
            }
        }
    }
}
