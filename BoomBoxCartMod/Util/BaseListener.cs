using BepInEx.Logging;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.InputSystem;
using UnityEngine;

namespace BoomBoxCartMod.Util
{
    public class BaseListener : MonoBehaviourPunCallbacks
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;

        public int lastUserAmount = 0;
        private List<int> modList = new List<int>();

        public bool audioMuted = false;
        private bool mutePressed = false;

        public static bool downloaderAvailable = true;

        public List<int> GetAllModUsers()
        {
            return modList;
        }

        public void AddModUser(int id)
        {
            if (!modList.Contains(id))
            {
                modList.Add(id);
            }
        }

        private void Update() // TODO: Do not do in gui while typing somehow
        {
            if (!mutePressed && Keyboard.current != null && Keyboard.current[Instance.GlobalMuteKey.Value].wasPressedThisFrame)
            {
                mutePressed = true;
                audioMuted = !audioMuted;
            }
            else if (mutePressed && (Keyboard.current == null || Keyboard.current[Instance.GlobalMuteKey.Value].wasReleasedThisFrame))
            {
                mutePressed = false;
            }
        }

        public static void RPC(PhotonView view, string methodName, RpcTarget target, params object[] parameters)
        {
            if (view == null)
                return;

            if (target == RpcTarget.All || target == RpcTarget.Others)
            {
                var modUsers = Instance.baseListener.GetAllModUsers();
                var players = PhotonNetwork.CurrentRoom.Players;

                foreach (var player in players)
                {
                    if (modUsers.Contains(player.Key) && (target == RpcTarget.All || player.Key != PhotonNetwork.LocalPlayer.ActorNumber))
                    {
                        view.RPC(methodName, player.Value, parameters);
                    }
                }
            }
            else
            {
                view.RPC(methodName, target, parameters);
            }
        }

        public static void RPC(PhotonView view, string methodName, Photon.Realtime.Player target, params object[] parameters)
        {
            if (view != null && Instance.baseListener.GetAllModUsers().Contains(target.ActorNumber))
                view.RPC(methodName, PhotonNetwork.MasterClient, parameters);
        }


        public static void ReportDownloaderStatus(bool available)
        {
            if (available == downloaderAvailable)
                return;

            Instance.logger.LogInfo($"Downloader status: {(available ? "Available" : "Unavailable")}");

            downloaderAvailable = available;

            if (!available)
            {
                Instance.modDisabled = true;
            }

            if (PhotonNetwork.IsMasterClient)
            {
                if (available)
                {
                    Instance.modDisabled = false;
                    Instance.baseListener.AddModUser(PhotonNetwork.LocalPlayer.ActorNumber);
                }
                else
                {
                    Instance.baseListener.modList.Remove(PhotonNetwork.LocalPlayer.ActorNumber);
                }
            }
            else if (PhotonNetwork.IsConnected)
            {
                Instance.baseListener.photonView?.RPC(
                    "ModFeedbackCheck",
                    RpcTarget.MasterClient,
                    available ? BoomBoxCartMod.modVersion : "-1",
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }
        }

        public override void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            base.OnPlayerLeftRoom(otherPlayer);
            modList.Remove(otherPlayer.ActorNumber);

            Instance.logger.LogInfo($"Player {otherPlayer.ActorNumber} left the room.");
        }

        [PunRPC]
        public void ModFeedbackCheck(string modVersion, int actorNumber)
        {
            if (Instance.baseListener == null)
                return;


            if (PhotonNetwork.IsMasterClient)
            {
                if (Instance.modDisabled)
                    return;

                if (modVersion == BoomBoxCartMod.modVersion)
                {
                    Instance.baseListener.AddModUser(actorNumber);
                    Instance.logger?.LogInfo($"Player {actorNumber} is using a compatible version of the mod.");
                }
                else
                {
                    Instance.baseListener.GetAllModUsers().Remove(actorNumber);
                    Instance.logger?.LogInfo($"Player {actorNumber} will not be joining the jam session.");
                }
                return;
            }
            else
            {
                Instance.modDisabled = !downloaderAvailable || (modVersion != BoomBoxCartMod.modVersion);
                Instance.logger.LogInfo($"Mod {(Instance.modDisabled ? "DISABLED" : "ENABLED")}. Current version: {BoomBoxCartMod.modVersion}, requested: {modVersion}");
                
                Instance.baseListener.photonView?.RPC(
                    "ModFeedbackCheck",
                    RpcTarget.MasterClient,
                    BaseListener.downloaderAvailable ? BoomBoxCartMod.modVersion : "-1",
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }
        }
    }
}
