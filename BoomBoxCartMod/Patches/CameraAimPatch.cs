using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace BoomBoxCartMod.Patches
{
    [HarmonyPatch(typeof(CameraAim))]
    class CamerAimPatch
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
        private static ManualLogSource Logger => Instance.logger;

        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        static bool PatchPlayerAim(CameraAim __instance)
        {
            return !BoomboxUI.showUI; // Prevent interacting when in UI
        }
    }
}
