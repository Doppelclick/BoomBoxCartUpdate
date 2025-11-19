using BepInEx.Logging;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BoomBoxCartMod.Util
{
    public class BaseListener : MonoBehaviourPunCallbacks
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;

        private List<int> modList = new List<int>();
        public static PhotonView photonView;


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
                /*
                if (Instance.modDisabled)
                {
                    //Possibly disable other parts of the mod here
                }
                */
            }
        }
    }
}
