using Photon.Pun;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using BoomBoxCartMod.Util;
using System;
using BepInEx.Logging;
using System.Linq;
using static BoomBoxCartMod.Boombox;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

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

        private static bool mutePressed = false;
        private const string SharedStatePropertyPrefix = "boomboxState.";

        private bool isApplyingSharedState = false;
        private string lastSharedStateJson = null;
        private string pendingCurrentSongDownloadUrl = null;
        private int lastAppliedSharedStateVersion = -1;

        [Serializable]
        private class SharedBoomboxState
        {
            public int version;
            public string key;
            public bool isPlaying;
            public bool pendingPlaybackStart;
            public float absVolume;
            public bool loopQueue;
            public int currentSongIndex = -1;
            public int playbackStartTimestamp;
            public int playbackTime;
            public string[] queueTitles = Array.Empty<string>();
            public string[] queueUrls = Array.Empty<string>();
            public int[] queueStartTimes = Array.Empty<int>();
            public SharedAudioEntry[] queue = Array.Empty<SharedAudioEntry>();
        }

        [Serializable]
        private class SharedAudioEntry
        {
            public string title;
            public string url;
            public int startTime;
        }

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
            audioSource.mute = Instance.baseListener.audioMuted;
            lowPassFilter = gameObject.AddComponent<AudioLowPassFilter>();
            lowPassFilter.enabled = false;

            downloadHelper = gameObject.AddComponent<DownloadHelper>();

            UpdateAudioRangeBasedOnVolume(audioSource.volume);

            //Logger.LogInfo($"AudioSource: {audioSource}");
            photonView = GetComponent<PhotonView>();
            //Logger.LogInfo($"PhotonView: {photonView}");

            isAwaitingSyncPlayback = data.pendingPlaybackStart;

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

        private void Start()
        {
            ApplyVisualStateFromData();
            LoadSharedStateFromRoom();
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

            bool songReachedEnd = audioSource.clip != null
                && audioSource.time >= Math.Max(0f, audioSource.clip.length - 0.05f);

            if (syncFinished && data.isPlaying && !audioSource.isPlaying && songReachedEnd && PhotonNetwork.IsMasterClient) // Song finished playing
            {
                int currentIndex = GetCurrentSongIndex();
                if (currentIndex == -1)
                {
                    DismissQueueLocal();
                }
                else if (currentIndex + 1 >= data.playbackQueue.Count)
                {
                    if (LoopQueue && data.playbackQueue.Count > 0)
                    {
                        SelectSongIndex(0);
                    }
                    else
                    {
                        data.currentSong = null;
                        data.isPlaying = false;
                        SetPlaybackReferenceFromSeconds(0f);
                        PublishSharedState();
                    }
                }
                else
                {
                    SelectSongIndex(currentIndex + 1);
                }
            }
        }

        public void TogglePlaying(bool value)
        {
            data.isPlaying = value;
        }

        public static long GetCurrentTimeMilliseconds()
        {
            return PhotonNetwork.ServerTimestamp;
        }

        public long GetRelativePlaybackMilliseconds()
        {
            return GetCurrentTimeMilliseconds() - (long)Math.Round(GetTrackedPlaybackSeconds() * 1000f);
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

        private string GetSharedStatePropertyKey()
        {
            return SharedStatePropertyPrefix + photonView.ViewID;
        }

        private SharedBoomboxState CreateSharedState()
        {
            return new SharedBoomboxState
            {
                version = data.stateVersion,
                key = data.key,
                isPlaying = data.currentSong != null && data.isPlaying,
                pendingPlaybackStart = data.currentSong != null && data.pendingPlaybackStart,
                absVolume = data.absVolume,
                loopQueue = data.loopQueue,
                currentSongIndex = GetCurrentSongIndex(),
                playbackStartTimestamp = data.playbackStartTimestamp,
                playbackTime = data.playbackTime,
                queueTitles = data.playbackQueue.Select(entry => entry.Title).ToArray(),
                queueUrls = data.playbackQueue.Select(entry => entry.Url).ToArray(),
                queueStartTimes = data.playbackQueue.Select(entry => entry.StartTime).ToArray(),
                queue = data.playbackQueue.Select(entry => new SharedAudioEntry
                {
                    title = entry.Title,
                    url = entry.Url,
                    startTime = entry.StartTime
                }).ToArray()
            };
        }

        private List<AudioEntry> CreateQueueFromState(SharedBoomboxState state)
        {
            if (state.queueUrls != null && state.queueUrls.Length > 0)
            {
                List<AudioEntry> queueFromArrays = new List<AudioEntry>(state.queueUrls.Length);

                for (int index = 0; index < state.queueUrls.Length; index++)
                {
                    string url = state.queueUrls[index];
                    string title = state.queueTitles != null && index < state.queueTitles.Length && !string.IsNullOrWhiteSpace(state.queueTitles[index])
                        ? state.queueTitles[index]
                        : "Unknown Title";
                    int startTime = state.queueStartTimes != null && index < state.queueStartTimes.Length
                        ? state.queueStartTimes[index]
                        : 0;

                    if (!string.IsNullOrWhiteSpace(url) && !DownloadHelper.songTitles.ContainsKey(url))
                    {
                        DownloadHelper.songTitles[url] = title;
                    }

                    queueFromArrays.Add(new AudioEntry(title, url)
                    {
                        StartTime = startTime
                    });
                }

                return queueFromArrays;
            }

            if (state.queue == null)
            {
                return new List<AudioEntry>();
            }

            return state.queue.Select(entry =>
            {
                string title = string.IsNullOrWhiteSpace(entry.title) ? "Unknown Title" : entry.title;
                string url = entry.url;

                if (!string.IsNullOrWhiteSpace(url) && !DownloadHelper.songTitles.ContainsKey(url))
                {
                    DownloadHelper.songTitles[url] = title;
                }

                return new AudioEntry(title, url)
                {
                    StartTime = entry.startTime
                };
            }).ToList();
        }

        private void LoadSharedStateFromRoom()
        {
            if (!PhotonNetwork.IsConnected || PhotonNetwork.CurrentRoom == null || photonView == null)
            {
                return;
            }

            if (PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(GetSharedStatePropertyKey(), out object rawValue)
                && rawValue is string json
                && !string.IsNullOrWhiteSpace(json))
            {
                ApplySharedStateJson(json);
                return;
            }

            if (PhotonNetwork.IsMasterClient)
            {
                PublishSharedState(true);
            }
        }

        public override void OnRoomPropertiesUpdate(PhotonHashtable propertiesThatChanged)
        {
            base.OnRoomPropertiesUpdate(propertiesThatChanged);

            if (photonView == null || propertiesThatChanged == null)
            {
                return;
            }

            if (propertiesThatChanged.TryGetValue(GetSharedStatePropertyKey(), out object rawValue)
                && rawValue is string json
                && !string.IsNullOrWhiteSpace(json))
            {
                ApplySharedStateJson(json);
            }
        }

        private void ApplySharedStateJson(string json)
        {
            SharedBoomboxState state;

            try
            {
                state = JsonUtility.FromJson<SharedBoomboxState>(json);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to deserialize boombox state for cart {photonView?.ViewID}: {ex.Message}");
                return;
            }

            if (state == null)
            {
                Logger.LogWarning($"Deserialized boombox state for cart {photonView?.ViewID} is null.");
                return;
            }

            if (state.version < lastAppliedSharedStateVersion)
            {
                Logger.LogInfo($"Ignoring older boombox state for cart {photonView?.ViewID}. Behind by {lastAppliedSharedStateVersion - state.version} versions.");
                return;
            }

            lastSharedStateJson = json;
            lastAppliedSharedStateVersion = state.version;
            ApplySharedState(state);
        }

        private void ApplySharedState(SharedBoomboxState state)
        {
            int previousIndex = GetCurrentSongIndex();
            string previousUrl = data.currentSong?.Url;

            isApplyingSharedState = true;

            try
            {
                data.key = string.IsNullOrWhiteSpace(state.key) ? data.key : state.key;
                data.stateVersion = Math.Max(data.stateVersion, state.version);
                data.playbackQueue = CreateQueueFromState(state);
                data.absVolume = Mathf.Clamp01(state.absVolume);
                data.loopQueue = state.loopQueue;
                data.playbackTime = Math.Max(0, state.playbackTime);
                data.playbackStartTimestamp = state.playbackStartTimestamp;

                if (state.currentSongIndex >= 0 && state.currentSongIndex < data.playbackQueue.Count)
                {
                    data.currentSong = data.playbackQueue[state.currentSongIndex];
                }
                else
                {
                    data.currentSong = null;
                }

                data.pendingPlaybackStart = data.currentSong != null && state.pendingPlaybackStart;
                data.isPlaying = data.currentSong != null && state.isPlaying;

                float actualVolume = data.absVolume * data.personalVolumePercentage;
                audioSource.volume = actualVolume;
                UpdateAudioRangeBasedOnVolume(actualVolume);
                ApplyVisualStateFromData();

                bool songChanged = previousIndex != state.currentSongIndex || previousUrl != data.currentSong?.Url;
                ApplySharedPlaybackState(songChanged);

                GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
                UpdateStatusFromState();
                syncFinished = true;
            }
            finally
            {
                isApplyingSharedState = false;
            }
        }

        private void ApplySharedPlaybackState(bool songChanged)
        {
            if (data.currentSong?.Url == null)
            {
                isAwaitingSyncPlayback = false;
                data.pendingPlaybackStart = false;
                StopLocalPlayback(false);
                UpdateUIStatus("Ready to play music! Enter a Video URL");
                return;
            }

            AudioClip clip = data.currentSong.GetAudioClip();

            if (clip == null)
            {
                StopLocalPlayback(false);
                isAwaitingSyncPlayback = data.pendingPlaybackStart;
                startPlayBackOnDownload = data.pendingPlaybackStart;

                if (songChanged || data.pendingPlaybackStart)
                {
                    UpdateUIStatus($"Loading: {data.currentSong.Title}");
                }

                if (!data.pendingPlaybackStart)
                {
                    EnsureCurrentSongDownloaded();
                }
                return;
            }

            isAwaitingSyncPlayback = data.pendingPlaybackStart;

            if (audioSource.clip != clip)
            {
                audioSource.clip = clip;
                SetQuality(qualityLevel);
                UpdateAudioRangeBasedOnVolume(audioSource.volume);
            }

            SetPlaybackTime(data.playbackStartTimestamp);

            if (data.pendingPlaybackStart)
            {
                if (audioSource.isPlaying)
                {
                    audioSource.Pause();
                }

                UpdateUIStatus($"Loading: {data.currentSong.Title}");
            }
            else if (data.isPlaying)
            {
                if (!audioSource.isPlaying)
                {
                    audioSource.Play();
                }

                UpdateUIStatus($"Now playing: {data.currentSong.Title}");
            }
            else
            {
                if (audioSource.isPlaying)
                {
                    audioSource.Pause();
                }

                UpdateUIStatus($"Ready to play: {data.currentSong.Title}");
            }
        }

        public void ApplyVisualStateFromData()
        {
            VisualEffects effects = GetComponent<VisualEffects>();
            if (effects == null)
            {
                effects = gameObject.AddComponent<VisualEffects>();
            }

            effects.SetLights(data.underglowEnabled);

            Visualizer currentVisualizer = GetComponent<Visualizer>();
            if (data.visualizerEnabled)
            {
                if (currentVisualizer == null)
                {
                    currentVisualizer = gameObject.AddComponent<Visualizer>();
                }

                currentVisualizer.audioSource = audioSource;
                visualizer = currentVisualizer;
            }
            else if (currentVisualizer != null)
            {
                Destroy(currentVisualizer);
                visualizer = null;
            }
        }

        private async void EnsureCurrentSongDownloaded()
        {
            string url = data.currentSong?.Url;

            if (string.IsNullOrWhiteSpace(url)
                || downloadHelper == null
                || DownloadHelper.downloadedClips.ContainsKey(url)
                || pendingCurrentSongDownloadUrl == url)
            {
                return;
            }

            pendingCurrentSongDownloadUrl = url;

            try
            {
                await downloadHelper.StartAudioDownload(url);
            }
            finally
            {
                if (pendingCurrentSongDownloadUrl == url)
                {
                    pendingCurrentSongDownloadUrl = null;
                }
            }

            if (this == null || data.currentSong?.Url != url)
            {
                return;
            }

            ApplySharedPlaybackState(false);
        }

        private void UpdateStatusFromState()
        {
            if (data.currentSong == null)
            {
                UpdateUIStatus("Ready to play music! Enter a Video URL");
            }
            else if (data.pendingPlaybackStart)
            {
                UpdateUIStatus($"Loading: {data.currentSong.Title}");
            }
            else if (data.isPlaying)
            {
                UpdateUIStatus($"Now playing: {data.currentSong.Title}");
            }
            else
            {
                UpdateUIStatus($"Ready to play: {data.currentSong.Title}");
            }
        }

        public void PublishSharedState(bool force = false)
        {
            if (isApplyingSharedState
                || !PhotonNetwork.IsConnected
                || PhotonNetwork.CurrentRoom == null
                || photonView == null)
            {
                return;
            }

            if (PhotonNetwork.IsMasterClient)
            {
                data.stateVersion++;
            }

            SharedBoomboxState state = CreateSharedState();
            string json = JsonUtility.ToJson(state);
            if (!force && json == lastSharedStateJson)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    data.stateVersion--;
                }
                return;
            }

            PhotonHashtable properties = new PhotonHashtable
            {
                { GetSharedStatePropertyKey(), json }
            };

            PhotonNetwork.CurrentRoom.SetCustomProperties(properties);
            lastSharedStateJson = json;
            syncFinished = true;
        }

        private void StopLocalPlayback(bool updateSharedFlag)
        {
            if (audioSource.isPlaying)
            {
                audioSource.Stop();
            }

            if (updateSharedFlag)
            {
                TogglePlaying(false);
            }

            audioSource.clip = null;
        }

        private void SetPlaybackReferenceFromSeconds(float seconds)
        {
            data.playbackStartTimestamp = (int)(GetCurrentTimeMilliseconds() - Math.Round(Math.Max(0f, seconds) * 1000f));
        }

        private float GetTrackedPlaybackSeconds()
        {
            if (audioSource?.clip != null)
            {
                if (!audioSource.isPlaying && data.playbackTime > 0 && audioSource.time <= 0f)
                {
                    return data.playbackTime;
                }

                return audioSource.time;
            }

            if (data.playbackTime > 0)
            {
                return data.playbackTime;
            }

            return Math.Max(0f, (GetCurrentTimeMilliseconds() - data.playbackStartTimestamp) / 1000f);
        }

        private void CleanupCurrentPlayback()
        {
            StopLocalPlayback(true);
        }

        private bool ShouldRequestMasterMutation()
        {
            return PhotonNetwork.IsConnected && PhotonNetwork.CurrentRoom != null && !PhotonNetwork.IsMasterClient;
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

            if (audioSource.clip != clip)
            {
                CleanupCurrentPlayback(); // Probably unnecessary
                audioSource.clip = clip;
                SetQuality(qualityLevel);
                UpdateAudioRangeBasedOnVolume(audioSource.volume);
                audioSource.Play(); // Need to call twice for some reason for MasterClient only autoresume
            }
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

        public void EnqueueSongLocal(string url, int seconds)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestEnqueueSong),
                    RpcTarget.MasterClient,
                    url,
                    seconds,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            string title = DownloadHelper.songTitles.ContainsKey(url) ? DownloadHelper.songTitles[url] : "Unknown Title";
            AudioEntry song = new AudioEntry(title, url)
            {
                StartTime = seconds
            };

            data.playbackQueue.Add(song);

            if (data.currentSong == null)
            {
                data.currentSong = song;
                data.isPlaying = false;
                data.pendingPlaybackStart = true;
                isAwaitingSyncPlayback = PhotonNetwork.IsMasterClient;
                startPlayBackOnDownload = true;
                SetPlaybackReferenceFromSeconds(song.PeekStartTime(this));
            }

            PublishSharedState();

            if (PhotonNetwork.IsMasterClient)
            {
                downloadHelper.EnqueueDownload(url);
                downloadHelper.StartDownloadJob();
            }
        }

        public void CommitPlaybackSeek()
        {
            if (data.currentSong == null)
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestCommitPlaybackSeek),
                    RpcTarget.MasterClient,
                    audioSource.time,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            SetPlaybackReferenceFromSeconds(audioSource.time);
            PublishSharedState();
        }

        public void SetPlaybackStateLocal(bool startPlaying)
        {
            if (data.currentSong == null)
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestSetPlaybackState),
                    RpcTarget.MasterClient,
                    startPlaying,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            if (data.currentSong.GetAudioClip() == null)
            {
                startPlayBackOnDownload = startPlaying;
                data.isPlaying = false;
                data.pendingPlaybackStart = startPlaying;
                isAwaitingSyncPlayback = PhotonNetwork.IsMasterClient && data.pendingPlaybackStart;
                PublishSharedState();
                return;
            }

            float trackedPlaybackSeconds = GetTrackedPlaybackSeconds();
            SetPlaybackReferenceFromSeconds(trackedPlaybackSeconds);

            if (startPlaying && data.playbackTime > 0 && Math.Abs(trackedPlaybackSeconds - data.playbackTime) < 0.01f)
            {
                data.playbackTime = 0;
            }

            data.isPlaying = startPlaying;
            data.pendingPlaybackStart = false;
            PublishSharedState();
        }

        public void JumpPlaybackBySeconds(float seconds)
        {
            if (data.currentSong == null)
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestJumpPlaybackBySeconds),
                    RpcTarget.MasterClient,
                    seconds,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            float targetSeconds = Math.Max(0f, GetTrackedPlaybackSeconds() + seconds);
            if (audioSource?.clip != null)
            {
                targetSeconds = Math.Min(targetSeconds, Math.Max(0f, audioSource.clip.length - 0.05f));
            }

            SetPlaybackReferenceFromSeconds(targetSeconds);
            data.pendingPlaybackStart = false;
            PublishSharedState();
        }

        public void SelectSongIndex(int index)
        {
            if (index < 0 || index >= data.playbackQueue.Count)
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestSelectSongIndex),
                    RpcTarget.MasterClient,
                    index,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            data.currentSong = data.playbackQueue[index];
            data.isPlaying = false;
            data.pendingPlaybackStart = true;
            isAwaitingSyncPlayback = PhotonNetwork.IsMasterClient;
            startPlayBackOnDownload = true;
            SetPlaybackReferenceFromSeconds(data.currentSong.PeekStartTime(this));
            PublishSharedState();

            if (PhotonNetwork.IsMasterClient)
            {
                downloadHelper.DismissDownloadQueue();
                downloadHelper.DownloadQueue(index);
            }
        }

        public void SetVolumeLocal(float volume)
        {
            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestSetVolume),
                    RpcTarget.MasterClient,
                    volume,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            data.absVolume = Mathf.Clamp01(volume);
            float actualVolume = data.absVolume * data.personalVolumePercentage;
            audioSource.volume = actualVolume;
            UpdateAudioRangeBasedOnVolume(actualVolume);
            PublishSharedState();
        }

        public void SetLoopQueueLocal(bool loop)
        {
            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestSetLoopQueue),
                    RpcTarget.MasterClient,
                    loop,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            data.loopQueue = loop;
            PublishSharedState();
        }

        public void SetUnderglowEnabledLocal(bool enabled)
        {
            data.underglowEnabled = enabled;
            ApplyVisualStateFromData();
            GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
        }

        public void SetVisualizerEnabledLocal(bool enabled)
        {
            data.visualizerEnabled = enabled;
            ApplyVisualStateFromData();
            GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
        }

        public void DismissQueueLocal()
        {
            if (!PhotonNetwork.IsMasterClient && Instance.MasterClientDismissQueue.Value)
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestDismissQueue),
                    RpcTarget.MasterClient,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            StopLocalPlayback(false);
            data.playbackQueue.Clear();
            data.currentSong = null;
            data.isPlaying = false;
            data.pendingPlaybackStart = false;
            data.playbackTime = 0;
            SetPlaybackReferenceFromSeconds(0f);
            PublishSharedState();

            if (PhotonNetwork.IsMasterClient)
            {
                downloadHelper.DismissDownloadQueue();
                downloadHelper.ForceCancelDownload();
            }

            UpdateUIStatus("Ready to play music! Enter a Video URL");
        }

        public void MoveQueueItemLocal(int index, int newIndex)
        {
            if (index < 0 || index >= data.playbackQueue.Count || newIndex < 0 || newIndex >= data.playbackQueue.Count || index == newIndex)
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestMoveQueueItem),
                    RpcTarget.MasterClient,
                    index,
                    newIndex,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            AudioEntry tempSong = data.playbackQueue[index];
            data.playbackQueue.RemoveAt(index);
            data.playbackQueue.Insert(newIndex, tempSong);
            PublishSharedState();
        }

        public void RemoveQueueItemLocal(int index)
        {
            if (index < 0 || index >= data.playbackQueue.Count)
            {
                return;
            }

            if (ShouldRequestMasterMutation())
            {
                BaseListener.RPC(
                    photonView,
                    nameof(RequestRemoveQueueItem),
                    RpcTarget.MasterClient,
                    index,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            AudioEntry removedSong = data.playbackQueue[index];
            bool removedCurrentSong = ReferenceEquals(removedSong, data.currentSong);

            data.playbackQueue.RemoveAt(index);

            if (removedCurrentSong)
            {
                if (data.playbackQueue.Count == 0)
                {
                    data.currentSong = null;
                    data.isPlaying = false;
                    data.pendingPlaybackStart = false;
                    SetPlaybackReferenceFromSeconds(0f);
                }
                else
                {
                    int replacementIndex = Math.Min(index, data.playbackQueue.Count - 1);
                    if (index >= data.playbackQueue.Count && LoopQueue)
                    {
                        replacementIndex = 0;
                    }

                    data.currentSong = data.playbackQueue[replacementIndex];
                    data.isPlaying = false;
                    data.pendingPlaybackStart = true;
                    isAwaitingSyncPlayback = PhotonNetwork.IsMasterClient;
                    startPlayBackOnDownload = true;
                    SetPlaybackReferenceFromSeconds(data.currentSong.PeekStartTime(this));
                }
            }

            if (PhotonNetwork.IsMasterClient && data.currentSong?.Url != null)
            {
                downloadHelper.DismissDownloadQueue();
                downloadHelper.DownloadQueue(GetCurrentSongIndex());
            }

            PublishSharedState();
        }

        public void FinalizePendingPlaybackStart(bool startPlaying)
        {
            if (data.currentSong == null)
            {
                return;
            }

            isAwaitingSyncPlayback = false;
            data.pendingPlaybackStart = false;
            data.isPlaying = startPlaying;
            SetPlaybackReferenceFromSeconds(data.currentSong.UseStartTime(this));
            startPlayBackOnDownload = true;
            PublishSharedState(true);
        }

        [PunRPC]
        public void RequestEnqueueSong(string url, int seconds, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            EnqueueSongLocal(url, seconds);
        }

        [PunRPC]
        public void RequestCommitPlaybackSeek(float playbackSeconds, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient || data.currentSong == null)
            {
                return;
            }

            SetPlaybackReferenceFromSeconds(playbackSeconds);
            PublishSharedState();
        }

        [PunRPC]
        public void RequestSetPlaybackState(bool startPlaying, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            SetPlaybackStateLocal(startPlaying);
        }

        [PunRPC]
        public void RequestJumpPlaybackBySeconds(float seconds, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            JumpPlaybackBySeconds(seconds);
        }

        [PunRPC]
        public void RequestSelectSongIndex(int index, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            SelectSongIndex(index);
        }

        [PunRPC]
        public void RequestSetVolume(float volume, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            SetVolumeLocal(volume);
        }

        [PunRPC]
        public void RequestSetLoopQueue(bool loop, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            SetLoopQueueLocal(loop);
        }

        [PunRPC]
        public void RequestDismissQueue(int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            DismissQueueLocal();
        }

        [PunRPC]
        public void RequestMoveQueueItem(int index, int newIndex, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            MoveQueueItemLocal(index, newIndex);
        }

        [PunRPC]
        public void RequestRemoveQueueItem(int index, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            RemoveQueueItemLocal(index);
        }

        public void HandleDownloadedCurrentSong()
        {
            if (data.currentSong?.GetAudioClip() == null)
            {
                return;
            }

            ApplySharedPlaybackState(false);
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

        public override void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            base.OnPlayerEnteredRoom(newPlayer);

            if (PhotonNetwork.IsMasterClient && !Instance.modDisabled)
            {
                Instance.baseListener.photonView?.RPC(
                    "ModFeedbackCheck",
                    newPlayer,
                    BoomBoxCartMod.modVersion,
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
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
            photonView.RefreshRpcMonoBehaviourCache();
        }

        public void ResetData()
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            Logger.LogInfo($"Resetting Boombox {photonView.ViewID}");

            isAwaitingSyncPlayback = false;
            startPlayBackOnDownload = true;
            data.pendingPlaybackStart = false;
            UpdateUIStatus("Ready to play music! Enter a Video URL");
            CleanupCurrentPlayback();
            downloadHelper.DismissDownloadQueue();

            var dataStore = Instance.data.GetBoomboxData();
            int index = dataStore.Count;

            if (dataStore.Contains(data)) {
                index = dataStore.IndexOf(data);
                dataStore.Remove(data);
            }

            int oldVersion = data.stateVersion;
            data = new BoomboxData();
            data.stateVersion = oldVersion + 1;
            ApplyVisualStateFromData();

            if (Instance.RestoreBoomboxes.Value)
            {
                dataStore.Insert(index, data);
            }

            PublishSharedState(true);
        }

        public class BoomboxData
        {
            public string key = Guid.NewGuid().ToString();
            public AudioEntry currentSong = null;
            public List<AudioEntry> playbackQueue = new List<AudioEntry>();
            public bool isPlaying = false;
            public float absVolume = 0.6f;
            public float personalVolumePercentage = 0.35f; // make the range smaller than 0 to 1.0 volume! Allows players to set their volume % individually
            public bool loopQueue = false;
            public bool underglowEnabled = false;
            public bool visualizerEnabled = true;
            public bool pendingPlaybackStart = false;
            public int stateVersion = 0;

            public int playbackTime = 0;
            public int playbackStartTimestamp = 0;
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

            public int PeekStartTime(Boombox boombox)
            {
                if (boombox.data != null && boombox.data.playbackTime != 0) // Restoring from previous level, otherwise playbacktime == 0
                {
                    return boombox.data.playbackTime;
                }

                return StartTime;
            }

            // Used to sync the start time of a song if the url entered included a timestamp
            public int UseStartTime(Boombox boombox)
            {
                int re = PeekStartTime(boombox);
                if (boombox.data != null && boombox.data.playbackTime != 0)
                {
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
