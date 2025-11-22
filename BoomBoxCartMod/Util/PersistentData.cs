using BepInEx.Logging;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BoomBoxCartMod.Util
{
    public class PersistentData
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;

        private List<Boombox> initializedBoomBoxes = new List<Boombox>();
        private List<Boombox.BoomboxData> boomboxData = new List<Boombox.BoomboxData>();

        public static bool GetBoomboxViewStatus(Player player, int viewID)
        {
            string valName = "boomboxView" + viewID;

            if (player.CustomProperties.TryGetValue(valName, out object value))
            {
                if (value is bool status)
                {
                    return status;
                }
                else
                {
                    return false;
                }
            }

            return false;
        }

        public static void SetBoomboxViewInitialized(int viewID)
        {
            string valName = "boomboxView" + viewID;

            var props = new ExitGames.Client.Photon.Hashtable();
            props.Add(valName, true);

            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        }

        public static void RemoveBoomboxViewInitialized(int viewID)
        {
            string valName = "boomboxView" + viewID;

            var props = new ExitGames.Client.Photon.Hashtable();
            props.Add(valName, null);

            PhotonNetwork.LocalPlayer.SetCustomProperties(props);

            Instance.logger.LogInfo("Still has property after deletion: " + GetBoomboxViewStatus(PhotonNetwork.LocalPlayer, viewID));
        }

        public List<Boombox> GetAllBoomboxes()
        {
            return initializedBoomBoxes;
        }

        public List<Boombox.BoomboxData> GetBoomboxData()
        {
            return boomboxData;
        }

        public void InitializeBoomboxData(Boombox boombox)
        {
            int index = initializedBoomBoxes.IndexOf(boombox);
            if (index == -1)
            {
                index = initializedBoomBoxes.Count;
                initializedBoomBoxes.Add(boombox);
            }

            Boombox.BoomboxData data;
            if (PhotonNetwork.IsMasterClient && Instance.RestoreBoomboxes.Value)
            {
                if (index < boomboxData.Count)
                {
                    data = boomboxData[index];
                }
                else
                {
                    data = new Boombox.BoomboxData();
                    boomboxData.Add(data);
                }
            }
            else
            {
                data = new Boombox.BoomboxData();
            }

            boombox.data = data;
        }
    }
}
