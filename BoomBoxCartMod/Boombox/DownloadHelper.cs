using BepInEx.Logging;
using BoomBoxCartMod.Util;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using static BoomBoxCartMod.Boombox;
using UnityEngine.Networking;
using System.IO;
using BepInEx;

namespace BoomBoxCartMod
{
    public class DownloadHelper : MonoBehaviourPunCallbacks
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
        private static ManualLogSource Logger => Instance.logger;

        private Boombox boomboxParent = null;

        // caches the AudioClips in memory using the URL as the key.
        public static Dictionary<string, AudioClip> downloadedClips = new Dictionary<string, AudioClip>();

        // cache for song titles
        public static Dictionary<string, string> songTitles = new Dictionary<string, string>();

        // tracks players ready and errors during/after download phase
        public static Dictionary<string, HashSet<int>> downloadsReady = new Dictionary<string, HashSet<int>>();
        public static Dictionary<string, HashSet<int>> downloadErrors = new Dictionary<string, HashSet<int>>();

        private const float DOWNLOAD_TIMEOUT = 40f; // 40 seconds timeout for downloads
        private const float TIMEOUT_THRESHOLD = 10f; // 10 seconds to wait after partial consensus to finish download process
        private Dictionary<string, Coroutine> timeoutCoroutines = new Dictionary<string, Coroutine>();

        private Queue<string> downloadJobQueue = new Queue<string>();
        private bool isProcessingQueue = false;
        private string currentRequestId = null;
        private string currentDownloadUrl = null;
        private bool isTimeoutRecovery = false;
        private Coroutine processingCoroutine; // Should only be used by master client

        // all valid URL's to donwload audio from
        private static readonly Regex[] supportedVideoUrlRegexes = new[]
        {
		// YouTube TODO:/Youtube Music URLs
		new Regex(
            @"^(?<CleanedUrl>((?:https?:)?\/\/)?(((?:www|m)\.)?((?:youtube(?:-nocookie)?\.com|youtu\.be))|music\.youtube\.com)(\/(?:[\w\-]+\?v=|embed\/|live\/|v\/)?)([\w\-]+))(?<TrailingParams>(&(\S+&)*?(t=(?<TimeStamp>(?<Seconds>\d+)))\S*)?\S*?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
            ),
    
		// RuTube URLs
		new Regex(
            @"^(?<CleanedUrl>((?:https?:)?\/\/)?((?:www)?\.?)(rutube\.ru)(\/video\/)([\w\-]+))(?<TrailingParams>(\?(?:\S+&)*?(t=(?<TimeStamp>(?<Seconds>\d+)))\S*)?\S*?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
            ),
    
		// Yandex Music URLs
		new Regex(
            @"^(?<CleanedUrl>((?:https?:)?\/\/)?((?:www)?\.?)(music\.yandex\.ru)(\/album\/\d+\/track\/)([\w\-]+))(?<TrailingParams>(?:\?(\S+&)*?(t=(?<TimeStamp>(?<Seconds>\d+)))\S*)?\S*?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
            ),
    
		// Bilibili URLs
		new Regex(
            @"^(?<CleanedUrl>((?:https?:)?\/\/)?((?:www|m)\.)?(bilibili\.com)(\/video\/)([\w\-]+))(?<TrailingParams>(\?(?:\S+&)*?(t=(?<TimeStamp>(?<Seconds>\d+)))\S*)?\S*?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
            ),

		// SoundCloud URLs -- TODO: Shortened links, but will never support timestamp for those
		new Regex(
            @"^(?<CleanedUrl>((?:https?:)?\/\/)?((?:www|m)\.)?(soundcloud\.com|snd\.sc)\/([\w\-]+\/[\w\-]+))(?<TrailingParams>(?:\?(?:\S+&*?#)*?t=(?<TimeStamp>(?<Minutes>\d+)(?:\/|(?:%3A))(?<Seconds>\d{1,2})))?\S*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
            )
        };


        private void Awake()
        {
            boomboxParent = gameObject.GetComponent<Boombox>();
        }

        private void OnDestroy()
        {
            downloadJobQueue.Clear();
            foreach (var coroutine in timeoutCoroutines.Values)
            {
                if (coroutine != null)
                {
                    StopCoroutine(coroutine);
                }
            }
            timeoutCoroutines.Clear();
        }


        public bool IsProcessingQueue()
        {
            return isProcessingQueue;
        }

        public string GetCurrentDownloadUrl()
        {
            return currentDownloadUrl;
        }

        public static (string cleanedUrl, int seconds) IsValidVideoUrl(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                foreach (Regex regex in supportedVideoUrlRegexes)
                {
                    var match = regex.Match(url);
                    if (match.Success)
                    {
                        var cleanedUrl = match.Groups["CleanedUrl"];
                        if (cleanedUrl.Success)
                        {
                            var timeStamp = match.Groups["TimeStamp"];
                            int seconds = 0;
                            if (timeStamp.Success)
                            {
                                var secondsMatch = match.Groups["Seconds"];
                                var minutesMatch = match.Groups["Minutes"];
                                if (secondsMatch.Success && int.TryParse(secondsMatch.Value, out int secs))
                                {
                                    seconds = secs;
                                }
                                if (minutesMatch.Success && int.TryParse(minutesMatch.Value, out int mins))
                                {
                                    seconds += mins * 60;
                                }
                            }
                            return (cleanedUrl.Value, seconds); // Should be the cleaned url
                        }
                    }
                }
            }
            return (null, 0);
        }

        public static int CheckDownloadCount(string url, bool includeErrors = false)
        {
            if (string.IsNullOrEmpty(url))
                return 0;

            int re = 0;
            if (downloadsReady.ContainsKey(url))
            {
                re += downloadsReady[url].Count;
            }
            if (downloadErrors.ContainsKey(url))
            {
                re += downloadErrors[url].Count;
            }
            return re;
        }


        public void EnqueueDownload(string Url)
        {
            downloadJobQueue.Enqueue(Url);
        }

        public void DismissDownloadQueue()
        {
            downloadJobQueue.Clear();
        }

        public void StartDownloadJob()
        {
            if (!isProcessingQueue)
            {
                processingCoroutine = StartCoroutine(ProcessDownloadQueue());
            }
        }

        // size in MB is around 1.5 * minutes at max quality, at 10 Mbps or 1.25 MB/s
        public static float EstimateDownloadTimeSeconds(float songLength)
        {
            return (1.5f * songLength / 60f) / 1.25f; 
        }

        public void DownloadQueue(int startIndex)
        {
            if (startIndex < 0 || startIndex >= boomboxParent.data.playbackQueue.Count)
            {
                startIndex = 0;
            }

            for (int i = startIndex; i < boomboxParent.data.playbackQueue.Count; i++) // Songs in the queue after the current song
            {
                downloadJobQueue.Enqueue(boomboxParent.data.playbackQueue.ElementAt(i).Url);
            }
            for (int i = 0; i < startIndex; i++) // Songs in the queue from 0 to the current song
            {
                downloadJobQueue.Enqueue(boomboxParent.data.playbackQueue.ElementAt(i).Url);
            }

            StartDownloadJob();
        }

        [PunRPC]
        public void NotifyPlayersOfErrors(string message)
        {
            Logger.LogWarning(message);
            boomboxParent.UpdateUIStatus(message);
        }

        [PunRPC]
        public void ReportDownloadError(int actorNumber, string url, string errorMessage)
        {
            if (actorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                boomboxParent.UpdateUIStatus($"Error: {errorMessage}");
            }
            
            if (!PhotonNetwork.IsMasterClient)
                return;

            Logger.LogError($"Player {actorNumber} reported download error for {url}: {errorMessage}");

            if (!downloadErrors.ContainsKey(url))
                downloadErrors[url] = new HashSet<int>();

            downloadErrors[url].Add(actorNumber);
        }

        [PunRPC]
        public void SetSongTitle(string url, string title)
        {
            // Cache title
            DownloadHelper.songTitles[url] = title;

            if (boomboxParent.data.currentSong != null && boomboxParent.data.currentSong.Url == url)
            {
                boomboxParent.data.currentSong.Title = title;
                if (boomboxParent.data.currentSong.GetAudioClip() != null)
                {
                    boomboxParent.UpdateUIStatus($"Now playing: {title}");
                }
                else
                {
                    boomboxParent.UpdateUIStatus($"Loading: {title}");
                }
            }
        }

        private IEnumerator ProcessDownloadQueue()
        {
            isProcessingQueue = true;
            Logger.LogInfo("Master Client Download Queue Processor started.");

            while (downloadJobQueue.Count > 0 && isProcessingQueue)
            {
                string url = downloadJobQueue.Dequeue();

                // Yield execution until the download and sync process for this URL is complete.
                yield return MasterClientInitiateSync(url);
                if (this == null)
                {
                    yield break;
                }
            }
            if (this == null)
                yield break;

            // Queue is empty, stop the processor.
            isProcessingQueue = false;
            currentDownloadUrl = null;
            Logger.LogInfo("Master Client Download Queue Processor finished.");
        }

        private IEnumerator MasterClientInitiateSync(string url)
        {
            // Check if the song is still in the local playbackQueue (it might have been removed by a user)
            if (!boomboxParent.data.playbackQueue.Any(entry => entry.Url == url))
            {
                //Logger.LogInfo($"Skipping download for {url}, item removed from queue.");
                yield break;
            }

            string requestId = Guid.NewGuid().ToString();

            //if (isCurrentSong) isAwaitingSyncPlayback = true;
            currentDownloadUrl = url;
            currentRequestId = requestId;

            if (downloadsReady.ContainsKey(url))
            {
                if (downloadsReady[url].Count >= Instance.baseListener.GetAllModUsers().Count) // All players already have the song downloaded
                {
                    if (!boomboxParent.data.isPlaying && !boomboxParent.audioSource.isPlaying && boomboxParent.isAwaitingSyncPlayback && url == boomboxParent.data.currentSong?.Url)
                    {
                        boomboxParent.isAwaitingSyncPlayback = false;
                        BaseListener.RPC(
                            photonView, 
                            "PlayPausePlayback",
                            RpcTarget.All,
                            boomboxParent.startPlayBackOnDownload,
                            Boombox.GetCurrentTimeMilliseconds() - boomboxParent.data.currentSong.UseStartTime(boomboxParent) * 1000, 
                            PhotonNetwork.LocalPlayer.ActorNumber
                        ); // Should not do anything if there is already a song playing
                        boomboxParent.startPlayBackOnDownload = true;
                    }

                    yield break;
                }
                downloadsReady[url].Clear();
            }
            else
                downloadsReady[url] = new HashSet<int>();

            if (downloadErrors.ContainsKey(url))
                downloadErrors[url].Clear();
            else
                downloadErrors[url] = new HashSet<int>();

            // RPC call to start the download/sync process on ALL clients
            BaseListener.RPC(
                photonView, 
                "StartDownloadAndSync",
                RpcTarget.All,
                url,
                PhotonNetwork.LocalPlayer.ActorNumber
            );

            // Start timeout coroutine
            timeoutCoroutines[requestId] = StartCoroutine(DownloadTimeoutCoroutine(requestId, url));

            // Yield until the consensus is reached
            yield return WaitForPlayersReadyOrFailed(url);

            // --- Cleanup and Final Sync ---

            if (timeoutCoroutines?.TryGetValue(requestId, out Coroutine coroutine) == true && coroutine != null)
            {
                StopCoroutine(coroutine);
                timeoutCoroutines.Remove(requestId);
            }

            // Check if the URL we waited for is still the current download target
            if (currentDownloadUrl != url)
            {
                // Logger.LogInfo($"Consensus finished for {url}, but download target changed. Aborting sync.");
                yield break;
            }

            currentDownloadUrl = null;
            currentRequestId = null;

            Logger.LogInfo($"Consensus finished for {url}, Starting Playback.");

            if (boomboxParent?.isAwaitingSyncPlayback == true && url == boomboxParent.data.currentSong?.Url)
            {
                boomboxParent.isAwaitingSyncPlayback = false;
                BaseListener.RPC(
                    photonView, 
                    "PlayPausePlayback",
                    RpcTarget.All,
                    boomboxParent.startPlayBackOnDownload,
                    Boombox.GetCurrentTimeMilliseconds() - boomboxParent.data.currentSong.UseStartTime(boomboxParent) * 1000,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                boomboxParent.startPlayBackOnDownload = true;
            }
        }

        private IEnumerator DownloadTimeoutCoroutine(string requestId, string url)
        {
            yield return new WaitForSeconds(DOWNLOAD_TIMEOUT);

            if (currentRequestId == requestId && isProcessingQueue)
            {
                Logger.LogWarning($"Download timeout for url: {url}");

                Logger.LogInfo("Master client initiating timeout recovery");
                isTimeoutRecovery = true;

                foreach (var player in Instance.baseListener.GetAllModUsers())
                {
                    if (!downloadsReady[url].Contains(player))
                    {
                        if (!downloadErrors.ContainsKey(url))
                            downloadErrors[url] = new HashSet<int>();

                        downloadErrors[url].Add(player);
                        Logger.LogWarning($"Player {player} timed out during download");
                    }
                }

                if (downloadsReady.ContainsKey(url) && downloadsReady[url].Count > 0)
                {
                    string timeoutMessage = $"Some players timed out. Continuing playback for {downloadsReady[url].Count} players.";
                    BaseListener.RPC(
                        photonView, 
                        "NotifyPlayersOfErrors",
                        RpcTarget.All,
                        timeoutMessage
                    );

                    // initiate playback with the master client as requester to unblock the system
                    if (boomboxParent.isAwaitingSyncPlayback && boomboxParent.data.currentSong?.Url == url)
                    {
                        boomboxParent.isAwaitingSyncPlayback = false;
                        BaseListener.RPC(
                            photonView, 
                            "PlayPausePlayback",
                            RpcTarget.All,
                            boomboxParent.startPlayBackOnDownload,
                            Boombox.GetCurrentTimeMilliseconds() - boomboxParent.data.currentSong.UseStartTime(boomboxParent) * 1000,
                            PhotonNetwork.LocalPlayer.ActorNumber
                        );
                        boomboxParent.startPlayBackOnDownload = true;
                    }
                }
                else
                {
                    BaseListener.RPC(
                        photonView, 
                        "NotifyPlayersOfErrors",
                        RpcTarget.All,
                        "Download timed out for all players."
                    );
                }

                currentDownloadUrl = null;
                currentRequestId = null;
                isTimeoutRecovery = false;
            }

            timeoutCoroutines.Remove(requestId);
        }

        [PunRPC]
        public async void StartDownloadAndSync(string url, int requesterId)
        {
            if (url == null)
            {
                return;
            }

            if (currentDownloadUrl != url)
            {
                currentDownloadUrl = url;

                if (!downloadsReady.ContainsKey(url))
                    downloadsReady[url] = new HashSet<int>();
                if (!downloadErrors.ContainsKey(url))
                    downloadErrors[url] = new HashSet<int>();
            }

            if (downloadedClips.ContainsKey(url))
            {
                BaseListener.RPC(
                    photonView, 
                    "ReportDownloadComplete",
                    RpcTarget.MasterClient,
                    url,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            if (await StartAudioDownload(url) && this != null)
            {
                BaseListener.RPC(
                    photonView, 
                    "ReportDownloadComplete",
                    RpcTarget.MasterClient,
                    url,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }
        }

        [PunRPC]
        public void ReportDownloadComplete(string url, int actorNumber)
        {
            if (!PhotonNetwork.IsMasterClient || url == null)
                return;

            if (!downloadsReady.ContainsKey(url))
                downloadsReady[url] = new HashSet<int>();

            if (downloadErrors.ContainsKey(url) && downloadErrors[url].Contains(actorNumber))
                downloadErrors[url].Remove(actorNumber);

            if (!boomboxParent.isAwaitingSyncPlayback && boomboxParent.data.currentSong?.Url == url) // Handle e.g. late join
            {
                PhotonNetwork.CurrentRoom.Players.TryGetValue(actorNumber, out Player targetPlayer);
                if (!downloadsReady[url].Contains(actorNumber) && targetPlayer != null)
                {
                    Logger.LogInfo($"Player {actorNumber} reported ready late. Sending start playback.");

                    BaseListener.RPC(
                        photonView, 
                        "PlayPausePlayback",
                        targetPlayer,
                        boomboxParent.audioSource.isPlaying && boomboxParent.data.isPlaying,
                        boomboxParent.GetRelativePlaybackMilliseconds(),
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
            }
            
            if (!downloadsReady[url].Contains(actorNumber))
                downloadsReady[url].Add(actorNumber);
            //Logger.LogInfo($"Player {actorNumber} reported ready for url: {url}. Total ready: {downloadsReady[url].Count}");
        }

        private IEnumerator WaitForPlayersReadyOrFailed(string url)
        {
            int totalPlayers = Instance.baseListener.GetAllModUsers().Count;
            int readyCount = 0;
            int errorCount = 0;

            float waitTime = 0.1f;

            float? partialConsensusStartTime = null;

            // Wait until either all players are accounted for (ready + error = total) 
            // or we have at least one player ready and some have errors
            while (true && this != null)
            {
                readyCount = downloadsReady.ContainsKey(url) ? downloadsReady[url].Count : 0;
                errorCount = downloadErrors.ContainsKey(url) ? downloadErrors[url].Count : 0;

                //Logger.LogInfo($"{PhotonNetwork.LocalPlayer.ActorNumber}: Waiting for players to be ready. Ready: {readyCount}, Errors: {errorCount}, Total: {totalPlayers}");

                // Exit conditions:
                // 1. All players are accounted for (ready + errors = total)
                bool allAccountedFor = readyCount + errorCount >= totalPlayers;
                // 2. At least one player is ready and some have errors and we've waited a reasonable time
                bool partialConsensus = readyCount > 0 && errorCount > 0;

                bool timeOutReached = false;

                if (!partialConsensus) // If the condition is not met (e.g. a player reports ready, clearing the error count)
                {
                    partialConsensusStartTime = null;
                }
                else if (!partialConsensusStartTime.HasValue)
                {
                    partialConsensusStartTime = Time.time;
                }
                else
                {
                    timeOutReached = (Time.time - partialConsensusStartTime.Value) >= TIMEOUT_THRESHOLD;
                }


                if (allAccountedFor || timeOutReached)
                {
                    break;
                }

                yield return new WaitForSeconds(waitTime);
            }

            if (this != null)
                Logger.LogInfo($"Ready to proceed with playback. Ready: {readyCount}, Errors: {errorCount}, Total: {totalPlayers} for url: {url}");
        }

        public static async Task<AudioClip> GetAudioClipAsync(string filePath)
        {
            await Task.Yield(); // Render frame before running function

            if (!File.Exists(filePath))
            {
                throw new Exception($"Audio file not found at path: {filePath}");
            }

            //string escapedPath = Uri.EscapeDataString(filePath);
            //string uri = "file:///" + escapedPath.Replace("%5C", "/").Replace("%3A", ":");

            Uri fileUri = new Uri(filePath);
            string uri = fileUri.AbsoluteUri;

            //Logger.LogInfo($"Loading audio clip from: {uri}");

            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
            {
                www.timeout = (int)DOWNLOAD_TIMEOUT;
                ((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = true; // Eliminate stutter by loading the clip dynamically

                TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
                var operation = www.SendWebRequest();
                operation.completed += (asyncOp) =>
                {
                    if (www.result != UnityWebRequest.Result.Success)
                    {
                        Logger.LogError($"Web request failed: {www.error}, URI: {uri}");
                        tcs.SetException(new Exception($"Failed to load audio file: {www.error}"));
                    }
                    else
                        tcs.SetResult(true);
                };
                await tcs.Task;
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

                if (clip == null)
                {
                    throw new Exception("Failed to get AudioClip content.");
                }

                return clip;
            }
        }


        public async Task<bool> StartAudioDownload(string url)
        {
            if (!downloadedClips.ContainsKey(url))
            {
                try
                {
                    var title = await YoutubeDL.DownloadAudioTitleAsync(url);

                    songTitles[url] = title;

                    BaseListener.RPC(
                        photonView, 
                        "SetSongTitle",
                        RpcTarget.All,
                        url,
                        title
                    );
                    //Logger.LogInfo($"Set song title for url: {url} to {title}");

                    var filePath = await YoutubeDL.DownloadAudioAsync(url, title);

                    if (boomboxParent?.data?.currentSong?.Url == url)
                    {
                        boomboxParent?.UpdateUIStatus($"Processing audio: {title}");
                    }

                    AudioClip clip = await GetAudioClipAsync(filePath);

                    downloadedClips[url] = clip;
                    Logger.LogInfo($"Downloaded and cached clip for video: {title}");
                }
                catch (Exception ex)
                {
                    //Logger.LogError($"Failed to download audio: {ex.Message}");

                    if (boomboxParent?.data?.currentSong?.Url == url)
                    {
                        boomboxParent?.UpdateUIStatus($"Error: {ex.Message}");
                    }


                    /* // TODO: Somehow remove the song, as it was not downloaded correctly
                    if (PhotonNetwork.IsMasterClient)
                    {
                        BaseListener.RPC(
                            "RemoveQueueItem",
                            RpcTarget.All,
                            boomboxParent.GetCurrentSongIndex(),
                            PhotonNetwork.LocalPlayer.ActorNumber
                        );
                    }
                    */

                    // Report download error to other players

                    BaseListener.RPC(
                        photonView, 
                        "ReportDownloadError",
                        RpcTarget.All,
                        PhotonNetwork.LocalPlayer.ActorNumber,
                        url,
                        ex.Message
                    );

                    return false;
                }
            }
            /*
            else
            {
                Logger.LogInfo($"Clip already cached for url: {Url}");
            }
            */

            if (boomboxParent?.data?.playbackQueue != null)
            {
                foreach (AudioEntry song in boomboxParent.data.playbackQueue.FindAll(entry => entry.Url == url))
                {
                    if (songTitles.ContainsKey(url))
                        song.Title = songTitles[url];
                }
            }

            return true;
        }

        public void RemoveFromCache(string url)
        {
            if (downloadedClips.ContainsKey(url))
            {
                AudioClip clip = downloadedClips[url];
                downloadedClips.Remove(url);

                if (clip != null)
                {
                    Destroy(clip);
                }

                //Logger.LogInfo($"Removed clip for url: {url} from cache");
            }

            if (songTitles.ContainsKey(url))
            {
                songTitles.Remove(url);
            }

            if (downloadsReady.ContainsKey(url))
            {
                downloadsReady.Remove(url);
            }

            if (downloadErrors.ContainsKey(url))
            {
                downloadErrors.Remove(url);
            }
        }

        public override void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            // idek if this method works, never tested, but its here !
            if (isProcessingQueue)
            {
                if (!string.IsNullOrEmpty(currentDownloadUrl))
                {
                    if (downloadsReady.ContainsKey(currentDownloadUrl) &&
                        !downloadsReady[currentDownloadUrl].Contains(otherPlayer.ActorNumber))
                    {
                        if (!downloadErrors.ContainsKey(currentDownloadUrl))
                            downloadErrors[currentDownloadUrl] = new HashSet<int>();

                        downloadErrors[currentDownloadUrl].Add(otherPlayer.ActorNumber);
                        Logger.LogInfo($"Player {otherPlayer.ActorNumber} left during download - marking as error");
                    }
                }
            }

            base.OnPlayerLeftRoom(otherPlayer);
        }

        public void ForceCancelDownload()
        {
            if (isProcessingQueue)
            {
                string urlToReport = currentDownloadUrl;
                if (urlToReport == null)
                {
                    urlToReport = "Unknown";
                }
                currentDownloadUrl = null;

                // Clean up any running coroutines
                foreach (var coroutine in timeoutCoroutines.Values)
                {
                    if (coroutine != null)
                    {
                        StopCoroutine(coroutine);
                    }
                }
                timeoutCoroutines.Clear();

                // Download coroutine will stop if the downloadQueue is empty

                BaseListener.RPC(
                    photonView, 
                    "ReportDownloadError",
                    RpcTarget.All, 
                    PhotonNetwork.LocalPlayer.ActorNumber, 
                    urlToReport,
                    "Download cancelled."
                );

                Logger.LogInfo("Download was force cancelled by user.");
            }
        }
    }
}
