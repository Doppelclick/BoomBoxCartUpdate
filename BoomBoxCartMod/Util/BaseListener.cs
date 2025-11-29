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
        public static PhotonView photonView;

        private List<int> modList = new List<int>();

        public bool audioMuted = false;
        private bool mutePressed = false;

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

        private void Awake()
        {
            photonView = GetComponent<PhotonView>();
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

        public override void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            base.OnPlayerLeftRoom(otherPlayer);
            modList.Remove(otherPlayer.ActorNumber);

            Instance.logger.LogInfo($"Player {otherPlayer.ActorNumber} left the room.");
        }

        [PunRPC]
        public void ModFeedbackCheck(string modVersion, int actorNumber)
        {
            if (PhotonNetwork.IsMasterClient)
            {
                if (modVersion == BoomBoxCartMod.modVersion && Instance.baseListener != null)
                {
                    Instance.baseListener.AddModUser(actorNumber);
                    Instance.logger?.LogInfo($"Player {actorNumber} is using a compatible version of the mod.");
                }
                return;
            }
            else
            {
                Instance.modDisabled = modVersion != BoomBoxCartMod.modVersion;
                Instance.logger.LogInfo($"Mod {(Instance.modDisabled ? "DISABLED" : "ENABLED")}. Current version: {BoomBoxCartMod.modVersion}, requested: {modVersion}");
                
                photonView?.RPC(
                    "ModFeedbackCheck",
                    RpcTarget.MasterClient,
                    BoomBoxCartMod.modVersion,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                if (Instance.modDisabled)
                {
                    foreach (Boombox boombox in Instance.data.GetAllBoomboxes())
                    {
                        Destroy(boombox);
                    }
                    Instance.data.GetAllBoomboxes().Clear();
                    Instance.data.GetBoomboxData().Clear();
                }
            }
        }
    }
}
