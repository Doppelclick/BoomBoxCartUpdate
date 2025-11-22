using Photon.Pun;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using BoomBoxCartMod.Util;
using UnityEngine.Networking;
using System;
using System.IO;
using BepInEx.Logging;
using System.Text.RegularExpressions;
using System.Collections;
using System.Linq;
using System.Security.Policy;
using Photon.Realtime;
using UnityEngine.InputSystem;
using ExitGames.Client.Photon.StructWrapping;
using HarmonyLib;
using System.Reflection;

namespace BoomBoxCartMod
{
    public class Boombox : MonoBehaviourPunCallbacks
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
        private static ManualLogSource Logger => Instance.logger;

        public PhotonView photonView;
        public AudioSource audioSource;
        public Visualizer visualizer;

        public DownloadHelper downloadHelper = null;

        bool syncFinished = false; // Only applies to master client

        public float minDistance = 3f;
        public float maxDistanceBase = 15f;
        public float maxDistanceAddition = 30f;

        public bool isAwaitingSyncPlayback = false;
        public bool startPlayBackOnDownload = true;
        public BoomboxData data = new BoomboxData(); // Initialized using BaseListener.InitializeBoomboxData when BoomBox is created 

        private AudioLowPassFilter lowPassFilter;
        public static int qualityLevel = 4; // 0 lowest, 4 highest

        public static bool audioMuted = false;
        private static bool mutePressed = false;

        private void Awake()
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.volume = 0.15f;
            audioSource.spatialBlend = 1f;
            audioSource.playOnAwake = false;
            //audioSource.minDistance = 3f;
            //audioSource.maxDistance = 13f;
            //AnimationCurve curve = new AnimationCurve(
            //	new Keyframe(0f, 1f), // full volume at 0 distance
            //	//new Keyframe(3f, 0.8f),
            //	new Keyframe(6f, 1f),
            //	new Keyframe(10f, 0.5f),
            //	new Keyframe(13f, 0f) // fully silent at maxDistance
            //);
            //audioSource.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            audioSource.rolloffMode = AudioRolloffMode.Custom;
            audioSource.spread = 90f;
            audioSource.dopplerLevel = 0f;
            audioSource.reverbZoneMix = 1f;
            audioSource.spatialize = true;
            audioSource.loop = false; // Handled in Update() since we are using a queue
            audioSource.mute = audioMuted;
            lowPassFilter = gameObject.AddComponent<AudioLowPassFilter>();
            lowPassFilter.enabled = false;

            downloadHelper = gameObject.AddComponent<DownloadHelper>();

            UpdateAudioRangeBasedOnVolume(audioSource.volume);

            //Logger.LogInfo($"AudioSource: {audioSource}");
            photonView = GetComponent<PhotonView>();
            //Logger.LogInfo($"PhotonView: {photonView}");

            isAwaitingSyncPlayback = false;

            if (photonView == null)
            {
                Logger.LogError("PhotonView not found on Boombox object.");
                return;
            }

            // add BoomboxController component for handling temporary ownership
            if (GetComponent<BoomboxController>() == null)
            {
                gameObject.AddComponent<BoomboxController>();
                //Logger.LogInfo("BoomboxController component added to Boombox");
            }

            if (GetComponent<Visualizer>() == null)
            {
                visualizer = gameObject.AddComponent<Visualizer>();
                visualizer.audioSource = audioSource; // Explicitly set the audiosource
            }

            PersistentData.SetBoomboxViewInitialized(photonView.ViewID);

            Logger.LogInfo($"Boombox initialized on this cart. AudioSource: {audioSource}, PhotonView: {photonView}");
        }

        private void Update()
        {
            if (PhotonNetwork.IsMasterClient && data.isPlaying && MonstersCanHearMusic && EnemyDirector.instance != null)
            {
                monsterAttractTimer += Time.deltaTime;
                if (monsterAttractTimer >= monsterAttractInterval)
                {
                    EnemyDirector.instance.SetInvestigate(transform.position, 5f);
                    monsterAttractTimer = 0f;
                }
            }
            else
            {
                monsterAttractTimer = 0f;
            }

            if (Instance.baseListener.audioMuted != audioSource.mute)
            {
                audioSource.mute = Instance.baseListener.audioMuted;
            }

            if (syncFinished && data.isPlaying && !audioSource.isPlaying && PhotonNetwork.IsMasterClient) // Song finished playing
            {
                int currentIndex = GetCurrentSongIndex();
                CleanupCurrentPlayback();

                if (currentIndex == -1) // Current song not found in queue
                {
                    photonView.RPC(
                        "SyncPlayback",
                        RpcTarget.All,
                        -1,
                        Boombox.GetCurrentTimeMilliseconds(),
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
                else if (currentIndex + 1 >= data.playbackQueue.Count) // Current song is at end of queue, stop playback or loop to the start if loopQueue is enabled
                {
                    photonView.RPC(
                        "SyncPlayback",
                        RpcTarget.All,
                        LoopQueue ? 0 : -1,
                        Boombox.GetCurrentTimeMilliseconds(),
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
                else // Middle of queue, start playing the next song
                {
                    photonView.RPC(
                        "SyncPlayback",
                        RpcTarget.All,
                        currentIndex + 1,
                        Boombox.GetCurrentTimeMilliseconds(),
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
            }
        }

        public void TogglePlaying(bool value)
        {
            data.isPlaying = value;
        }

        public async void SyncInitializeWithOthers()
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            Logger.LogInfo("Syncing with others");

            if (Instance.RestoreBoomboxes.Value)
            {
                photonView.RPC( // Set data key, to restore to the same cart across clients
                    "SetData",
                    RpcTarget.Others,
                    data.key,
                    data.playbackQueue.Count,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }

            if (data.playbackQueue.Count > 0 && data.currentSong?.Url != null)
            {
                if (PhotonNetwork.IsMasterClient) 
                    isAwaitingSyncPlayback = true;
                startPlayBackOnDownload = data.isPlaying && Instance.AutoResume.Value;
                data.isPlaying = false;
                downloadHelper.DownloadQueue(GetCurrentSongIndex());
            }
            else
            {
                data.playbackQueue.Clear();
                data.currentSong = null;
            }
            /* Should not be necessary, since other clients do not restore the queue automatically
            else
            {
                photonView.RPC(
                    "DismissQueue",
                    RpcTarget.All,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }
            */

            syncFinished = true;
        }

        [PunRPC]
        public void SetData(string key, int queueSize, int requesterId)
        {
            if (requesterId != PhotonNetwork.MasterClient.ActorNumber)
                return;

            BoomboxData? foundData = Instance.data.GetBoomboxData().FirstOrDefault(data => data.key == key);
            if (foundData != null)
            {
                data = foundData;
            }
            else
            {
                data.key = key;
                Instance.data.GetBoomboxData().Add(data);
            }

            if (data.playbackQueue.Count < queueSize)
            {
                photonView.RPC(
                    "RequestFullSync",
                    RpcTarget.MasterClient,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }

            TogglePlaying(false);
        }

        public static long GetCurrentTimeMilliseconds()
        {
            return PhotonNetwork.ServerTimestamp;
        }

        public long GetRelativePlaybackMilliseconds()
        {
            return GetCurrentTimeMilliseconds() - (long)Math.Round(audioSource.time * 1000f);
        }

        private float monsterAttractTimer = 0f;
        private float monsterAttractInterval = 1.0f; // every second

        public static string GetSongTitle(string url)
        {
            if (DownloadHelper.songTitles.ContainsKey(url))
            {
                return DownloadHelper.songTitles[url];
            }
            return null;
        }

        public int GetCurrentSongIndex()
        {
            if (data.currentSong == null)
            {
                return -1;
            }
            return data.playbackQueue.IndexOf(data.currentSong);
        }

        private void CleanupCurrentPlayback()
        {
            if (audioSource.isPlaying)
                audioSource.Stop();
            TogglePlaying(false);

            audioSource.clip = null;
        }

        public void StartPlayBack()
        {
            //Logger.LogInfo("StartPlayBack() called");
            AudioClip clip = data.currentSong?.GetAudioClip();

            if (clip == null)
            {
                Logger.LogError("Clip not found for current song");
                CleanupCurrentPlayback();
                return;
            }

            if (audioSource.clip == clip)
            {
                audioSource.Play();
                TogglePlaying(true);
                return;
            }

            CleanupCurrentPlayback(); // Probably unnecessary
            audioSource.clip = clip;
            SetQuality(qualityLevel);
            UpdateAudioRangeBasedOnVolume(audioSource.volume);
            audioSource.Play();
            TogglePlaying(true);

            //Logger.LogInfo($"StartPlayBack() finished, clip={audioSource.clip != null} volume={audioSource.volume} playing={audioSource.isPlaying}");
        }

        public void PausePlayBack()
        {
            audioSource.Pause();
            TogglePlaying(false);
        }

        //(long)(Boombox.GetCurrentTimeMilliseconds() - Math.Round(timeInSeconds * 1000f)) // Need to round
        public void SetPlaybackTime(long startTimeMillis)
        {
            audioSource.time = Math.Max(0f, (GetCurrentTimeMilliseconds() - startTimeMillis)) / 1000f;
        }

        [PunRPC]
        public async void RequestSong(string url, int seconds, int requesterId)
        {
            //Logger.LogInfo($"RequestSong RPC received: url={url}, sedonds={seconds}, requesterId={requesterId}");
            if (url == null)
                return;

            /* TODO: Add an option to allow / prohibit duplicates in the queue
			if (!playbackQueue.Any(entry => entry.Url == url))
			{ }
			*/

            bool ownRequest = requesterId == PhotonNetwork.LocalPlayer.ActorNumber;

            /* Should not be necessary
            (string cleanedUrl, int seconds) = DownloadHelper.IsValidVideoUrl(url);
            if (string.IsNullOrEmpty(cleanedUrl))
			{
				Logger.LogError($"Invalid Video URL: {url}");
				if (ownRequest)
				{
					UpdateUIStatus("Error: Invalid Video URL.");
				}
				return;
			}
			*/

            AudioEntry song = new AudioEntry((DownloadHelper.songTitles.ContainsKey(url) ? DownloadHelper.songTitles[url] : "Unknown Title"), url);
            song.StartTime = seconds;
            data.playbackQueue.Add(song);

            //bool isCurrentSong = false;
            if (data.currentSong == null) // Start playback if this is the first song added to the queue
            {
                //isCurrentSong = true;
                data.currentSong = song;
                if (PhotonNetwork.IsMasterClient)
                    isAwaitingSyncPlayback = true;
            }

            if (!PhotonNetwork.IsMasterClient)
                return;

            downloadHelper.EnqueueDownload(url);
            downloadHelper.StartDownloadJob();
        }

        [PunRPC]
        public void SyncQueue(int currentIndex, Dictionary<string, object>[] queueObject, int requesterId) // Syncs the queue, but presumes no changes to the current song were made
        {
            if (requesterId != PhotonNetwork.MasterClient.ActorNumber) // Should not be necessary, but why not
                return;

            List<AudioEntry> queue = DeserializeAudioEntryArray(queueObject);

            Logger.LogInfo($"SyncQueue RPC received: currentIndex={currentIndex}, queueSize={queue.Count}");

            data.playbackQueue = queue;
            foreach (AudioEntry song in data.playbackQueue)
            {
                if (!DownloadHelper.songTitles.ContainsKey(song.Url))
                {
                    DownloadHelper.songTitles[song.Url] = song.Title;
                }
            }            
            
            if (currentIndex == -1) // Used to stop playback
            {
                isAwaitingSyncPlayback = false;
                data.currentSong = null;
                CleanupCurrentPlayback();
                return;
            }
            else if (currentIndex >= data.playbackQueue.Count) // Invalid
            {
                Logger.LogError($"SyncQueue RPC received with wrong index: newSongIndex={currentIndex}, songCount={data.playbackQueue.Count}, requesterId={requesterId}");
                return;
            }

            data.currentSong = queue[currentIndex];
        }


        [PunRPC]
        public void DismissQueue(int requesterId)
        {
            if (requesterId != PhotonNetwork.MasterClient.ActorNumber) // Only allow the Host to dismiss the queue
            {
                if (PhotonNetwork.IsMasterClient && !Instance.MasterClientDismissQueue.Value) // Only depends on the host's config setting
                {
                    photonView.RPC(
                        "DismissQueue",
                        RpcTarget.All,
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
                return;
            }

            if (audioSource.isPlaying)
            {
                CleanupCurrentPlayback();
                //Logger.LogInfo($"Playback stopped by player {requesterId}");
                UpdateUIStatus("Ready to play music! Enter a Video URL");
            }
            data.playbackQueue.Clear();
            data.currentSong = null;

            if (PhotonNetwork.IsMasterClient)
            {
                downloadHelper.DismissDownloadQueue();
                downloadHelper.ForceCancelDownload();
            }
        }

        [PunRPC]
        public void MoveQueueItem(int index, int newIndex, int requesterId) // Moves an item in the queue
        {
            //Logger.LogInfo($"MoveQueueItem RPC received: index={index}, newIndex={newIndex}");

            if (index < 0 || index >= data.playbackQueue.Count || newIndex < 0 || newIndex >= data.playbackQueue.Count || index == newIndex)
            {
                Logger.LogError($"MoveQueueItem RPC received with invalid indices: {index}, {newIndex}, queue count: {data.playbackQueue.Count}");
                return;
            }

            AudioEntry tempSong = data.playbackQueue[index];
            data.playbackQueue.RemoveAt(index);
            data.playbackQueue.Insert(newIndex, tempSong);

            if (!PhotonNetwork.IsMasterClient || DownloadHelper.CheckDownloadCount(data.currentSong.Url, false) >= Instance.baseListener.GetAllModUsers().Count)
                return;

            downloadHelper.DismissDownloadQueue();
            downloadHelper.DownloadQueue(GetCurrentSongIndex()); // Not ideal, but best i can be asked to do for now. Will check if songs have been previously downloaded, before trying to download them anyway.
        }

        [PunRPC]
        public void RemoveQueueItem(int index, int requesterId) // Removes an item from the queue
        {
            if (index < 0 || index > data.playbackQueue.Count)
            {
                Logger.LogError($"RemoveQueueItem RPC received with wrong index: index={index}, songCount={data.playbackQueue.Count}, requesterId={requesterId}");
                return;
            }
            //Logger.LogInfo($"RemoveQueueItem RPC received: index={index}");

            int currentIndex = GetCurrentSongIndex();
            data.playbackQueue.RemoveAt(index);

            if (index == currentIndex) // Should never happen, but just to make sure
            {
                CleanupCurrentPlayback();
                data.currentSong = null;

                if (PhotonNetwork.IsMasterClient)
                {
                    if (data.playbackQueue.Count > 0)
                    {
                        index %= data.playbackQueue.Count; // currentSongIndex == index
                        if (currentIndex < data.playbackQueue.Count || LoopQueue) // If the song was not the last in the queue or we want to loop
                        {
                            photonView.RPC(
                                "SyncPlayback",
                                RpcTarget.All,
                                index,
                                Boombox.GetCurrentTimeMilliseconds(),
                                PhotonNetwork.LocalPlayer.ActorNumber
                            );
                            return;
                        }
                    }

                    photonView.RPC(
                        "SyncPlayback",
                        RpcTarget.All,
                        -1,
                        Boombox.GetCurrentTimeMilliseconds(),
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
            }
        }

        [PunRPC]
        public async void SyncPlayback(int newSongIndex, long startTime, int requesterId) // Syncs the currently playing song, but presumes no changes to the queue were made
        {
            if (newSongIndex == -1) // Used to stop playback
            {
                isAwaitingSyncPlayback = false;
                data.currentSong = null;
                CleanupCurrentPlayback();
                return;
            }
            else if (newSongIndex >= data.playbackQueue.Count) // Invalid
            {
                if (!PhotonNetwork.IsMasterClient)
                {
                    photonView.RPC(
                        "RequestFullSync",
                        RpcTarget.MasterClient,
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
                Logger.LogError($"SyncPlayback RPC received with wrong index: newSongIndex={newSongIndex}, songCount={data.playbackQueue.Count}, startTime={startTime}, requesterId={requesterId}");
                return;
            }

            //Logger.LogInfo($"SyncPlayback RPC received: newSongIndex={newSongIndex}, startTime={startTime}, requesterId={requesterId}, queueSize={playbackQueue.Count}");


            if (GetCurrentSongIndex() == newSongIndex)
            {
                if (!data.isPlaying || !audioSource.isPlaying) // TODO: Possibly do not start playing the song when the time of the song is changed, as this should be handled by PlayPausePlayback
                {
                    StartPlayBack();
                }
                SetPlaybackTime(startTime);
                return;
            }
            else if (data.isPlaying || audioSource.isPlaying) // Changing to new song
            {
                CleanupCurrentPlayback();
            }

            AudioEntry oldSong = data.currentSong;
            data.currentSong = data.playbackQueue.ElementAt(newSongIndex);

            if (!PhotonNetwork.IsMasterClient) // Handle starting to play a new song from here
                return;

            if (data.currentSong?.Url == null) // Should never happen
            {
                photonView.RPC(
                    "RemoveQueueItem",
                    RpcTarget.All,
                    newSongIndex,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            if (DownloadHelper.CheckDownloadCount(data.currentSong.Url, false) < Instance.baseListener.GetAllModUsers().Count) // The download process will play the song once it is finished
            {
                // TODO: add proper logic to where the song should be added, instead of just iterating over the whole queue
                if (data.currentSong.Url != downloadHelper.GetCurrentDownloadUrl())
                    downloadHelper.DismissDownloadQueue();
                isAwaitingSyncPlayback = true;
                if (data.currentSong.Url != downloadHelper.GetCurrentDownloadUrl())
                    downloadHelper.DownloadQueue(newSongIndex);
            }
            else
            {
                photonView.RPC(
                    "PlayPausePlayback",
                    RpcTarget.All,
                    true,
                    Boombox.GetCurrentTimeMilliseconds() - data.currentSong.UseStartTime(this) * 1000,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }
        }

        [PunRPC]
        public void PlayPausePlayback(bool startPlaying, long startTime, int requesterId)
        {
            if (data.playbackQueue.Count == 0 || data.currentSong?.GetAudioClip() == null)
            {
                return;
            }

            //Logger.LogInfo($"PlayPausePlayBack RPC received: startPlaying={startPlaying}, startTime={startTime}, requesterId={requesterId} -- currentSong={currentSong != null}, playbackQueueSize={playbackQueue.Count}");

            if (startPlaying != data.isPlaying)
            {
                string playPauseText;
                if (startPlaying)
                {
                    if (data.currentSong == null)
                    {
                        if (PhotonNetwork.IsMasterClient)
                        {
                            photonView.RPC(
                                "SyncPlayback",
                                RpcTarget.All,
                                -1,
                                Boombox.GetCurrentTimeMilliseconds(),
                                PhotonNetwork.LocalPlayer.ActorNumber
                            );
                        }
                        return;
                    }
                    StartPlayBack();
                    playPauseText = "Started";
                }
                else
                {
                    PausePlayBack();
                    playPauseText = "Paused";
                }
                //Logger.LogInfo($"Playback {playPauseText} by player {requesterId}");
                //UpdateUIStatus($"{playPauseText} Playback");
            }
            else if (!startPlaying)
            {
                StartPlayBack();
                PausePlayBack();
            }

            SetPlaybackTime(startTime);

            if (data.isPlaying)
            {
                UpdateUIStatus($"Now playing: {(data.currentSong?.Url == null ? "Unkown" : data.currentSong?.Title)}");

                if (PhotonNetwork.IsMasterClient && MonstersCanHearMusic
                    && EnemyDirector.instance != null && data.currentSong != null && transform?.position != null
                ) {
                    EnemyDirector.instance.SetInvestigate(transform.position, 5f);
                }
            }
        }

        public void SetQuality(int level)
        {
            qualityLevel = Mathf.Clamp(level, 0, 4);

            switch (qualityLevel)
            {
                case 0: // hella low
                    lowPassFilter.enabled = true;
                    lowPassFilter.cutoffFrequency = 1500f;
                    break;
                case 1: // low quality
                    lowPassFilter.enabled = true;
                    lowPassFilter.cutoffFrequency = 3000f;
                    break;
                case 2: // medium-low quality
                    lowPassFilter.enabled = true;
                    lowPassFilter.cutoffFrequency = 4500f;
                    break;
                case 3: // medium-high (default rn)
                    lowPassFilter.enabled = true;
                    lowPassFilter.cutoffFrequency = 6000f;
                    break;
                case 4: // highest quality
                    lowPassFilter.enabled = false;
                    break;
            }

            //Logger.LogInfo($"Audio quality set to level {qualityLevel}");
        }

        [PunRPC]
        public void UpdateQuality(int level, int requesterId)
        {
            BoomboxController controller = GetComponent<BoomboxController>();

            SetQuality(level);
            //Logger.LogInfo($"Quality updated to level {level} by player {requesterId}");
        }


        [PunRPC]
        public void UpdateVolume(float volume, int requesterId)
        {
            BoomboxController controller = GetComponent<BoomboxController>();

            data.absVolume = volume;
            float actualVolume = volume * data.personalVolumePercentage;
            audioSource.volume = actualVolume;
            UpdateAudioRangeBasedOnVolume(actualVolume);
            //Logger.LogInfo($"Volume updated to {actualVolume} by player {requesterId}");
        }

        private void UpdateAudioRangeBasedOnVolume(float volume)
        {
            // louder volume = hear from farther away
            float newMaxDistance = Mathf.Lerp(maxDistanceBase, maxDistanceBase + maxDistanceAddition, volume); // More noticeable effect

            audioSource.minDistance = minDistance;
            audioSource.maxDistance = newMaxDistance;

            AnimationCurve curve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(minDistance, 0.9f),
                new Keyframe(newMaxDistance, 0f)
            );

            audioSource.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);

            //Logger.LogInfo($"Updated audio range based on volume {volume}: maxDistance={newMaxDistance}");
        }

        [PunRPC]
        public void UpdateLooping(bool loop, int requesterId) // TODO: imp
        {
            if (PhotonNetwork.MasterClient.ActorNumber == requesterId)
            {
                LoopQueue = loop;
                //Logger.LogInfo($"Volume looping to {loop} by player {requesterId}");
            }
        }

        public void UpdateUIStatus(string message)
        {
            if (this == null)
                return;
            BoomboxUI ui = GetComponent<BoomboxUI>();
            if (ui != null && ui.IsUIVisible())
            {
                ui.UpdateStatus(message);
            }
        }

        [PunRPC]
        public void RequestFullSync(int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            var player = PhotonNetwork.PlayerList.FirstOrDefault(player => player.ActorNumber == requesterId);
            if (player == null || !Instance.baseListener.GetAllModUsers().Contains(requesterId))
                return;

            HandleLateJoin(player);
        }

        public override void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            // no idea if this works, but ancitipating a late join mod

            base.OnPlayerEnteredRoom(newPlayer);

            if (PhotonNetwork.IsMasterClient)
            {
                Logger.LogInfo($"New player {newPlayer.ActorNumber} joined - syncing current playback state");

                Task.Run(async () =>
                {
                    BaseListener.photonView?.RPC(
                        "ModFeedbackCheck",
                        newPlayer,
                        BoomBoxCartMod.modVersion,
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                    float startTime = Time.time;
                    while (photonView != null && !PersistentData.GetBoomboxViewStatus(newPlayer, photonView.ViewID))
                    {
                        if (Time.time - startTime > 5) // Wait max 5 seconds
                        {
                            return;
                        }
                        await Task.Delay(200);
                    }
                    if (photonView != null && Instance.baseListener.GetAllModUsers().Contains(newPlayer.ActorNumber))
                    {
                        HandleLateJoin(newPlayer);
                    }
                });
            }
        }

        private async void HandleLateJoin(Photon.Realtime.Player player)
        {
            if (Instance.RestoreBoomboxes.Value)
            {
                photonView.RPC( // Set data key, to restore to the same cart across clients
                    "SetData",
                    player,
                    data.key,
                    0,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
            }

            photonView.RPC( // Set index and queue
                "SyncQueue",
                player,
                GetCurrentSongIndex(),
                SerializeAudioEntryList(data.playbackQueue).ToArray(),
                PhotonNetwork.LocalPlayer.ActorNumber
            );

            photonView.RPC(
                "UpdateLooping",
                player,
                LoopQueue,
                PhotonNetwork.LocalPlayer.ActorNumber
            );

            photonView.RPC(
                "UpdateVolume",
                player,
                data.absVolume,
                PhotonNetwork.LocalPlayer.ActorNumber
            );

            if (audioSource?.clip != null) 
            { // Not worth starting the download of the next song otherwise
                downloadHelper.DownloadQueue(GetCurrentSongIndex() + ((audioSource.time + 10 > audioSource.clip.length && audioSource.isPlaying) ? 1 : 0));
            }
        }

        private static bool applyQualityToDownloads = false;

        public static bool ApplyQualityToDownloads
        {
            get => applyQualityToDownloads;
            set => applyQualityToDownloads = value;
        }

        private static bool monstersCanHearMusic = false;

        public static bool MonstersCanHearMusic
        {
            get => monstersCanHearMusic;
            set => monstersCanHearMusic = value;
        }

        public bool LoopQueue
        {
            get => data.loopQueue;
            set => data.loopQueue = value;
        }

        private void OnDisable()
        {
            PersistentData.RemoveBoomboxViewInitialized(photonView.ViewID);
            data.playbackTime = (int)Math.Round(audioSource.time);
        }

        private void OnDestroy()
        {
            Instance.data.GetAllBoomboxes().Remove(this);
            Destroy(GetComponent<BoomboxUI>());
            Destroy(visualizer);
            Destroy(lowPassFilter);
            Destroy(audioSource);
            Destroy(downloadHelper);
            Destroy(gameObject.GetComponent<BoomboxController>());
        }

        public class BoomboxData
        {
            public string key = Guid.NewGuid().ToString();
            public AudioEntry currentSong = null;
            public List<AudioEntry> playbackQueue = new List<AudioEntry>();
            public bool isPlaying = false;
            public float absVolume = 0.19f;
            public float personalVolumePercentage = 0.8f; // make the range smaller than 0 to 1.0 volume! Allows players to set their volume % individually
            public bool loopQueue = false;

            public int playbackTime = 0;
        }

        public List<Dictionary<string, object>> SerializeAudioEntryList(List<AudioEntry> list)
        {
            List<Dictionary<string, object>> re = new List<Dictionary<string, object>>();

            foreach (AudioEntry entry in list)
            {
                Dictionary<string, object> audioEntry = new Dictionary<string, object>();
                audioEntry.Add("Title", entry.Title);
                audioEntry.Add("Url", entry.Url);
                audioEntry.Add("StartTime", entry.StartTime);
                re.Add(audioEntry);
            }

            return re;
        }

        public List<AudioEntry> DeserializeAudioEntryArray(Dictionary<string, object>[] array)
        {
            List<AudioEntry> re = new List<AudioEntry>();

            foreach (Dictionary<string, object> entry in array)
            {
                entry.TryGetValue("Title", out object titleObj);
                string title = titleObj as string ?? "Unknown";

                entry.TryGetValue("Url", out object urlObj);
                string url = urlObj as string;

                entry.TryGetValue("StartTime", out object startTimeObj);
                int startTime = startTimeObj as int? ?? 0;

                AudioEntry audioEntry = new AudioEntry(title, url);
                audioEntry.StartTime = startTime;

                re.Add(audioEntry);
            }

            return re;
        }

        public class AudioEntry
        {
            public string Title;
            public string Url;
            public int StartTime = 0;

            public AudioEntry(string title, string url)
            {
                Title = title;
                Url = url;
            }

            // Used to sync the start time of a song if the url entered included a timestamp
            public int UseStartTime(Boombox boombox)
            {
                int re = StartTime;
                if (boombox.data != null && boombox.data.playbackTime != 0) // Restoring from previous level, otherwise playbacktime == 0
                {
                    re = boombox.data.playbackTime;
                    boombox.data.playbackTime = 0;
                }
                else if (Instance.UseTimeStampOnce.Value)
                {
                    StartTime = 0;
                }
                return re;
            }

            public AudioClip GetAudioClip()
            {
                return DownloadHelper.downloadedClips.ContainsKey(Url) ? DownloadHelper.downloadedClips[Url] : null;
            }
        }
    }
}
