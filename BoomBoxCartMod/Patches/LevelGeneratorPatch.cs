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
            if (Instance.baseListener == null)
            {
                Instance.baseListener = __instance.gameObject.AddComponent<BaseListener>(); // TODO: Maybe do this differently
            }

            Logger.LogInfo("Level started generating, checking with other players if they are using the mod.");
            Instance.baseListener.GetAllModUsers().Clear();

            if (PhotonNetwork.IsMasterClient)
            {
                Instance.modDisabled = false;
                BaseListener.photonView?.RPC("ModFeedbackCheck", RpcTarget.OthersBuffered, BoomBoxCartMod.modVersion, PhotonNetwork.LocalPlayer.ActorNumber);
                Instance.baseListener.GetAllModUsers().Add(PhotonNetwork.LocalPlayer.ActorNumber);
            }
        }
    }
}
