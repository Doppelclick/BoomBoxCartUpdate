using Photon.Pun;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using BoomBoxCartMod.Util;
using System;
using BepInEx.Logging;
using System.Linq;
using Newtonsoft.Json;
using static BoomBoxCartMod.Boombox;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;
using System.Reflection;
using System.Security.Policy;
using System.Collections;

namespace BoomBoxCartMod
{
    public class Boombox : MonoBehaviourPunCallbacks
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
        private static ManualLogSource Logger => Instance.logger;

        public PhotonView photonView;
        public AudioPlayer audioPlayer;
        public Visualizer visualizer;
        public VisualEffects visualEffects;

        public DownloadHelper downloadHelper = null;

        bool syncFinished = false; // Only applies to master client

        public bool startPlayBackOnDownload = true;
        public BoomboxData data = new BoomboxData(); // Initialized using BaseListener.InitializeBoomboxData when BoomBox is created 

        private static bool mutePressed = false;

        private bool isApplyingSharedState = false;
        private string pendingCurrentSongDownloadUrl = null;
        private int lastAppliedSharedStateVersion = -1;
        private PhotonHashtable lastSharedState = null;

        private class SharedAudioEntry
        {

            public string title;
            public string url;
            public double duration;
            public int startTime;
        }

        private void Awake()
        {
            audioPlayer = gameObject.AddComponent<AudioPlayer>();

            downloadHelper = gameObject.AddComponent<DownloadHelper>();

            //Logger.LogInfo($"AudioSource: {audioSource}");
            photonView = GetComponent<PhotonView>();
            //Logger.LogInfo($"PhotonView: {photonView}");

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
                visualizer.audioSource = audioPlayer.audioSource; // Explicitly set the audiosource
            }

            if (GetComponent<VisualEffects>() == null)
            {
                visualEffects = gameObject.AddComponent<VisualEffects>();

            }

            PersistentData.SetBoomboxViewInitialized(photonView.ViewID);

            Logger.LogInfo($"Boombox initialized on this cart. AudioPlayer: {audioPlayer}, PhotonView: {photonView}");
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

            if (syncFinished && PhotonNetwork.IsMasterClient && data.isPlaying&& !audioPlayer.IsPlaying() && audioPlayer.SongReachedEnd()) // Song finished playing
            {
                int currentIndex = GetCurrentSongIndex();
                if (currentIndex == -1)
                {
                    DismissQueueLocal();
                    Logger.LogDebug("Dismiss queue, invalid current song.");
                }
                else if (currentIndex + 1 >= data.playbackQueue.Count)
                {
                    Logger.LogDebug("Finish playing song");
                    if (LoopQueue && data.playbackQueue.Count > 0)
                    {
                        SelectSongIndex(0);
                    }
                    else
                    {
                        data.currentSong = null;
                        data.playbackTime = 0;
                        data.isPlaying = false;
                        SetPlaybackReferenceFromSeconds(0f);
                        PublishSharedState(true);
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

        public static long GetCurrentServerTimeMilliseconds()
        {
            return PhotonNetwork.ServerTimestamp;
        }

        public long GetRelativePlaybackMilliseconds()
        {
            return GetCurrentServerTimeMilliseconds() - (long)Math.Round(GetTrackedPlaybackSeconds() * 1000f);
        }

        private float monsterAttractTimer = 0f;
        private const float monsterAttractInterval = 1.0f; // every second


        public int GetCurrentSongIndex()
        {
            if (data.currentSong == null)
            {
                return -1;
            }
            return data.playbackQueue.IndexOf(data.currentSong);
        }

        private string GetPropertyKey(string propertyName)
        {
            if (photonView == null)
            {
                return propertyName;
            }
            return $"boombox_{photonView.ViewID}_{propertyName}";
        }

        private PhotonHashtable CreateSharedState(bool includeTime, bool updateQueue) // TODO: Two same links playing at the same time causes errors.
        {

            PhotonHashtable table = new PhotonHashtable
            {
                { GetPropertyKey("propVersion"), data.stateVersion },
                { GetPropertyKey("IDKey"), data.key },
                { GetPropertyKey("isPlaying"), data.currentSong != null && data.isPlaying },
                { GetPropertyKey("pendingPlaybackStart"), data.pendingPlaybackStart },
                { GetPropertyKey("absVolume"), data.absVolume },
                { GetPropertyKey("loopQueue"), data.loopQueue },
                { GetPropertyKey("underglow"), data.underglowEnabled },
                { GetPropertyKey("visualizer"), data.visualizerEnabled },
                { GetPropertyKey("currentSongIndex"), GetCurrentSongIndex() }
            };

            if (includeTime)
            {
                // Don't always include as this causes audio stutters
                table.Add(GetPropertyKey("playbackStartTimestamp"), data.playbackStartTimestamp);
            }

            if (updateQueue)
            {
                table.Add(GetPropertyKey("queue"),
                    JsonConvert.SerializeObject(
                        data.playbackQueue.Select(entry => new SharedAudioEntry
                        {
                            title = entry.Title,
                            url = entry.Url,
                            duration = entry.Duration,
                            startTime = entry.StartTime
                        }).ToArray()
                        )
                );
                Logger.LogDebug($"Created table with queue size: {(JsonConvert.DeserializeObject<SharedAudioEntry[]>((string)table[GetPropertyKey("queue")])).Count()}");
            }
            return table;
        } 

        private void LoadSharedStateFromRoom()
        {
            if (!PhotonNetwork.IsConnected || PhotonNetwork.CurrentRoom == null || photonView == null)
            {
                return;
            }

            PhotonHashtable properties = PhotonNetwork.CurrentRoom.CustomProperties;

            ApplyChangedValues(properties);

            if (PhotonNetwork.IsMasterClient && properties.Count > 0)
            {
                PublishSharedState(true, true, true);
            }
        }

        public override void OnRoomPropertiesUpdate(PhotonHashtable propertiesThatChanged)
        {
            base.OnRoomPropertiesUpdate(propertiesThatChanged);

            if (photonView == null || propertiesThatChanged == null)
            {
                return;
            }

            ApplyChangedValues(propertiesThatChanged);
        }

        private void ApplyChangedValues(PhotonHashtable propertiesThatChanged)
        {
            isApplyingSharedState = true;

            string versionKey = GetPropertyKey("propVersion");
            if (!propertiesThatChanged.ContainsKey(versionKey))
            {
                isApplyingSharedState = false;
                return;
            }
            int version = (int)propertiesThatChanged[versionKey];

            if (version < data.stateVersion)
            {
                isApplyingSharedState = false;
                return;
            }


            int currentSongIndex = GetCurrentSongIndex();
            string queueKey = GetPropertyKey("queue");
            bool changedQueue = propertiesThatChanged.ContainsKey(queueKey);

            try // if statements, because they need to be in a certain order
            {
                if (changedQueue)
                {
                    string value = (string)propertiesThatChanged[queueKey];

                    SharedAudioEntry[] queue = JsonConvert.DeserializeObject<SharedAudioEntry[]>(value);

                    if (queue != null)
                    {
                        data.playbackQueue = queue.Select(entry =>
                        {
                            string title = string.IsNullOrWhiteSpace(entry.title) ? "Unknown Title" : entry.title;
                            double duration = entry.duration != null ? entry.duration : -1;
                            string url = entry.url;

                            SongInfo info = new SongInfo(title, duration);

                            return new AudioEntry(url, info)
                            {
                                StartTime = entry.startTime
                            };
                        }).ToList();
                        Logger.LogDebug($"Updated queue to size {data.playbackQueue.Count}.");
                    }
                    else
                    {
                        data.playbackQueue = new List<AudioEntry>();
                    }
                }

                string currentSongIndexKey = GetPropertyKey("currentSongIndex");
                if (propertiesThatChanged.ContainsKey(currentSongIndexKey))
                {
                    int index = (int)propertiesThatChanged[currentSongIndexKey];

                    if (index >= 0 && index < data.playbackQueue.Count)
                    {
                        data.currentSong = data.playbackQueue[index];
                    }
                    else
                    {
                        data.currentSong = null;
                        data.pendingPlaybackStart = false;
                        audioPlayer.Stop();
                        UpdateUIStatus("Ready to play music! Enter a Video URL");
                        Logger.LogDebug($"Invalid currentsong index: {index}.");
                    }
                }
                else if (changedQueue)
                {
                    // This is required, because currentsong is an object reference inside of playback queue, which we have modified - and took me way too long to figure out
                    data.currentSong = data.playbackQueue[currentSongIndex];
                }

                string pendingPlaybackStartKey = GetPropertyKey("pendingPlaybackStart");
                if (propertiesThatChanged.ContainsKey(pendingPlaybackStartKey))
                {
                    bool pendingStart = (bool)propertiesThatChanged[pendingPlaybackStartKey];
                    data.pendingPlaybackStart = pendingStart;
                }

                string isPlayingKey = GetPropertyKey("isPlaying");
                if (propertiesThatChanged.ContainsKey(isPlayingKey))
                {
                    bool playing = (bool)propertiesThatChanged[isPlayingKey];
                    data.isPlaying = data.currentSong != null && playing;
                }

                string absVolumeKey = GetPropertyKey("absVolume");
                if (propertiesThatChanged.ContainsKey(absVolumeKey))
                {
                    float val = (float)propertiesThatChanged[absVolumeKey];
                    float volume = Mathf.Clamp01(Convert.ToSingle(val));

                    if (data.absVolume != volume)
                    {
                        data.absVolume = volume;
                        float actualVolume = volume * data.personalVolumePercentage;
                        audioPlayer.SetVolume(actualVolume);
                    }
                }

                string loopQueueKey = GetPropertyKey("loopQueue");
                if (propertiesThatChanged.ContainsKey(loopQueueKey))
                {
                    bool loop = (bool)propertiesThatChanged[loopQueueKey];
                    data.loopQueue = loop;
                }

                string underglowKey = GetPropertyKey("underglow");
                if (propertiesThatChanged.ContainsKey(underglowKey) && Instance.SyncVisuals.Value)
                {
                    bool enabled = (bool)propertiesThatChanged[underglowKey];
                    data.underglowEnabled = enabled;
                    ApplyVisualStateFromData();
                    GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
                }

                string visualizerKey = GetPropertyKey("visualizer");
                if (propertiesThatChanged.ContainsKey(visualizerKey) && Instance.SyncVisuals.Value)
                {
                    bool enabled = (bool)propertiesThatChanged[visualizerKey];
                    data.visualizerEnabled = enabled;
                    ApplyVisualStateFromData();
                    GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
                }

                string playbackStartTimestampKey = GetPropertyKey("playbackStartTimestamp");
                if (propertiesThatChanged.ContainsKey(playbackStartTimestampKey))
                {
                    int time = (int)propertiesThatChanged[playbackStartTimestampKey];
                    data.playbackStartTimestamp = time;
                    SetPlaybackTime(time);
                }


                string idKeyKey = GetPropertyKey("IDKey");
                if (propertiesThatChanged.ContainsKey(idKeyKey))
                {
                    string k = (string)propertiesThatChanged[idKeyKey];
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        data.key = k;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Error occured while reading properties: {ex.Message}");
            }

            ApplySharedPlaybackState();

            GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();

            syncFinished = true;

            isApplyingSharedState = false;
        }

        private void ApplySharedPlaybackState()
        {
            if (data.currentSong?.Url == null)
            {
                Logger.LogDebug($"ApplySharedPlaybackState: No current song{(data.currentSong == null ? "" : "URL")}!");
                data.pendingPlaybackStart = false;
                audioPlayer.Stop();
                UpdateUIStatus("Ready to play music! Enter a Video URL");
                return;
            }


            if (data.currentSong?.ClipLoaded() != true)
            {
                audioPlayer.Stop();
                startPlayBackOnDownload = data.pendingPlaybackStart;

                if (data.pendingPlaybackStart)
                {
                    UpdateUIStatus($"Loading: {data.currentSong.Title}");
                }

                if (!data.pendingPlaybackStart)
                {
                    EnsureCurrentSongDownloaded();
                }
                return;
            }

            if (data.currentSong.Url != audioPlayer.currentUrl)
            {
                audioPlayer.Stop();
                audioPlayer.SetClip(data.currentSong.Url);
                audioPlayer.SetQuality(AudioPlayer.GetQuality());
                audioPlayer.UpdateAudioRangeBasedOnVolume();
            }

            ApplyVisualStateFromData();
            
            if (data.pendingPlaybackStart)
            {
                if (audioPlayer.IsPlaying())
                {
                    audioPlayer.Pause();
                }

                UpdateUIStatus($"Loading: {data.currentSong.Title}");
            }
            else if (data.isPlaying)
            {
                if (!audioPlayer.IsPlaying())
                {
                    Logger.LogDebug($"ApplySharedPlaybackState: Starting playback - timestamp={data.playbackStartTimestamp}");

                    audioPlayer.Play();
                    SetPlaybackTime(data.playbackStartTimestamp);

                    Logger.LogDebug($"ApplySharedPlaybackState: After Play(), audioPlayer.GetTime()={audioPlayer.GetTime()}");
                }

                UpdateUIStatus($"Now playing: {data.currentSong.Title}");
            }
            else
            {
                if (audioPlayer.IsPlaying())
                {
                    audioPlayer.Pause();
                }

                UpdateUIStatus($"Ready to play: {data.currentSong.Title}");
            }
        }

        public void ApplyVisualStateFromData()
        {
            if (visualEffects == null)
            {
                visualEffects = gameObject.AddComponent<VisualEffects>();

                visualizer.audioSource = audioPlayer.audioSource;
            }

            visualEffects.SetLights(data.underglowEnabled);

            if (data.visualizerEnabled)
            {
                if (visualizer == null)
                {
                    visualizer = gameObject.AddComponent<Visualizer>();
                }

                visualizer.audioSource = audioPlayer.audioSource;
            }
            else if (visualizer != null)
            {
                Destroy(visualizer);
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
                if (await downloadHelper.StartAudioDownload(url))
                {
                    BaseListener.RPC(
                        photonView,
                        nameof(DownloadHelper.ReportDownloadComplete),
                        RpcTarget.MasterClient,
                        url,
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                }
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

            ApplySharedPlaybackState();
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

        public bool CanPublish()
        {
            return !isApplyingSharedState
                && PhotonNetwork.IsConnected
                && PhotonNetwork.CurrentRoom != null
                && photonView != null;
        }

        public void PublishSharedState(bool updateTime, bool updateQueue = false, bool force = false)
        {
            if (!CanPublish())
                return;

            if (PhotonNetwork.IsMasterClient)
            {
                data.stateVersion++;
            }

            PhotonHashtable state = CreateSharedState(updateTime, updateQueue);

            PhotonNetwork.CurrentRoom.SetCustomProperties(state);
            lastSharedState = state;
            syncFinished = true;
        }

        private void StopLocalPlayback(bool updateSharedFlag)
        {
            audioPlayer.Stop();

            if (updateSharedFlag)
            {
                TogglePlaying(false);
            }
        }

        private void SetPlaybackReferenceFromSeconds(float totalSeconds)
        {
            float clampedSeconds = Math.Max(0f, totalSeconds);
            data.playbackStartTimestamp = (int)(GetCurrentServerTimeMilliseconds() - Math.Round(clampedSeconds * 1000f));
            Logger.LogDebug($"SetPlaybackReferenceFromSeconds: Set to {clampedSeconds}s, timestamp={data.playbackStartTimestamp}");
        }

        /*
         * @return song playback time in seconds, e.g. 60 for 60 seconds
         */
        private float GetTrackedPlaybackSeconds()
        {
            if (audioPlayer?.GetClip() != null)
            {
                if (!audioPlayer.IsPlaying() && data.playbackTime > 0 && audioPlayer.GetTime() <= 0f)
                {
                    Logger.LogDebug($"GetTrackedPlaybackSeconds: Returning cached playbackTime={data.playbackTime} (not playing, time <= 0)");
                    return data.playbackTime;
                }

                float audioTime = audioPlayer.GetTime();
                Logger.LogDebug($"GetTrackedPlaybackSeconds: Returning audioPlayer.GetTime()={audioTime}, isPlaying={audioPlayer.IsPlaying()}");
                return audioTime;
            }

            if (data.playbackTime > 0)
            {
                Logger.LogDebug($"GetTrackedPlaybackSeconds: Returning cached playbackTime={data.playbackTime} (no clip)");
                return data.playbackTime;
            }

            float calculatedTime = Math.Max(0f, (GetCurrentServerTimeMilliseconds() - data.playbackStartTimestamp) / 1000f);
            Logger.LogDebug($"GetTrackedPlaybackSeconds: Calculating from timestamp: {calculatedTime}s");
            return calculatedTime;
        }

        private void CleanupCurrentPlayback()
        {
            StopLocalPlayback(true);
        }

        private bool ShouldRequestMasterMutation()
        {
            return PhotonNetwork.IsConnected && PhotonNetwork.CurrentRoom != null && !PhotonNetwork.IsMasterClient;
        }

        //(long)(Boombox.GetCurrentTimeMilliseconds() - Math.Round(timeInSeconds * 1000f)) // Need to round
        public void SetPlaybackTime(long relativeStartTimeMillis)
        {
            float targetTime = Math.Max(0f, (GetCurrentServerTimeMilliseconds() - relativeStartTimeMillis) / 1000f);
            Logger.LogDebug($"SetPlaybackTime: Setting audioPlayer time to {targetTime}s (from timestamp {relativeStartTimeMillis})");
            audioPlayer.SetTime(targetTime);
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

            SongInfo info = DownloadHelper.songInfo.ContainsKey(url) ? DownloadHelper.songInfo[url] : new SongInfo();
            AudioEntry song = new AudioEntry(url, info)
            {
                StartTime = seconds
            };

            data.playbackQueue.Add(song);

            bool noCurrentSong = data.currentSong == null;

            if (noCurrentSong)
            {
                data.currentSong = song;
                data.playbackTime = 0;
                Logger.LogDebug($"Set currentsong local {data.currentSong}");
                StopLocalPlayback(true);
                data.pendingPlaybackStart = true;
                startPlayBackOnDownload = true;
                SetPlaybackReferenceFromSeconds(song.UseStartTime(this));
            }

            PublishSharedState(noCurrentSong, true);

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
                    audioPlayer.GetTime(),
                    PhotonNetwork.LocalPlayer.ActorNumber
                );
                return;
            }

            SetPlaybackReferenceFromSeconds(audioPlayer.GetTime());
            PublishSharedState(true);
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

            if (data.currentSong.ClipLoaded() != true)
            {
                startPlayBackOnDownload = startPlaying;
                data.isPlaying = false;
                data.pendingPlaybackStart = startPlaying;
                PublishSharedState(true);
                return;
            }

            // When pausing: preserve current playback position
            // When resuming: use stored playback position
            float trackedPlaybackSeconds = GetTrackedPlaybackSeconds();
            Logger.LogDebug($"SetPlaybackStateLocal: trackedPlaybackSeconds={trackedPlaybackSeconds}, isPlaying={startPlaying}");
            
            SetPlaybackReferenceFromSeconds(trackedPlaybackSeconds);

            Logger.LogDebug($"SetPlaybackStateLocal: Final state - isPlaying={startPlaying}, timestamp={data.playbackStartTimestamp}");
            data.isPlaying = startPlaying;
            data.pendingPlaybackStart = false;
            PublishSharedState(true, false, true);
        }

        public void JumpPlaybackBySeconds(float seconds)
        {
            if (data.currentSong.ClipLoaded() != true)
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
            if (audioPlayer?.GetClip() != null)
            {
                targetSeconds = Math.Min(targetSeconds, Math.Max(0f, audioPlayer.GetClip().length - 0.5f));
            }

            SetPlaybackReferenceFromSeconds(targetSeconds);
            data.pendingPlaybackStart = false;
            PublishSharedState(true);
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
            StopLocalPlayback(true);
            data.playbackTime = 0;
            data.pendingPlaybackStart = true;
            startPlayBackOnDownload = true;
            PublishSharedState(true);

            if (PhotonNetwork.IsMasterClient)
            {
                string url = data.currentSong?.Url;

                if (url != null && DownloadHelper.downloadsReady.ContainsKey(url) &&
                    DownloadHelper.downloadsReady[url].Count >= Instance.baseListener.GetAllModUsers().Count &&
                    data.pendingPlaybackStart)
                {
                    Logger.LogDebug("Skipping download queue, as all users are ready to play.");
                    FinalizePendingPlaybackStart(startPlayBackOnDownload);
                }
                else
                {
                    downloadHelper.DismissDownloadQueue();
                    downloadHelper.DownloadQueue(index);
                }
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
            audioPlayer.SetVolume(actualVolume);
            PublishSharedState(false);
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
            PublishSharedState(false);
        }

        public void SetUnderglowEnabledLocal(bool enabled)
        {
            if (Instance.SyncVisuals.Value)
            {
                if (ShouldRequestMasterMutation())
                {
                    BaseListener.RPC(
                        photonView,
                        nameof(RequestSetUnderglowEnabled),
                        RpcTarget.MasterClient,
                        enabled,
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                    return;
                }

                data.underglowEnabled = enabled;
                ApplyVisualStateFromData();
                GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
                PublishSharedState(false);
            }
            else
            {
                data.underglowEnabled = enabled;
                ApplyVisualStateFromData();
                GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
            }
        }

        public void SetVisualizerEnabledLocal(bool enabled)
        {
            if (Instance.SyncVisuals.Value)
            {
                if (ShouldRequestMasterMutation())
                {
                    BaseListener.RPC(
                        photonView,
                        nameof(RequestSetVisualizerEnabled),
                        RpcTarget.MasterClient,
                        enabled,
                        PhotonNetwork.LocalPlayer.ActorNumber
                    );
                    return;
                }

                data.visualizerEnabled = enabled;
                ApplyVisualStateFromData();
                GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
                PublishSharedState(false);
            }
            else
            {
                data.visualizerEnabled = enabled;
                ApplyVisualStateFromData();
                GetComponent<BoomboxUI>()?.UpdateDataFromBoomBox();
            }
        }

        public void DismissQueueLocal()
        {
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

            StopLocalPlayback(true);
            data.playbackQueue.Clear();
            data.currentSong = null;
            data.pendingPlaybackStart = false;
            data.playbackTime = 0;
            SetPlaybackReferenceFromSeconds(0f);
            PublishSharedState(true, true);

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
            PublishSharedState(false, true);
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
                    data.playbackTime = 0;
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
                    data.playbackTime = 0;
                    data.pendingPlaybackStart = true;
                    startPlayBackOnDownload = true;
                    SetPlaybackReferenceFromSeconds(data.currentSong.UseStartTime(this));
                }
            }

            if (PhotonNetwork.IsMasterClient && data.currentSong?.Url != null)
            {
                downloadHelper.DismissDownloadQueue();
                downloadHelper.DownloadQueue(GetCurrentSongIndex());
            }

            PublishSharedState(removedCurrentSong, true);
        }

        public void FinalizePendingPlaybackStart(bool startPlaying)
        {
            if (data.currentSong == null)
            {
                return;
            }

            data.pendingPlaybackStart = false;
            data.isPlaying = startPlaying;
            SetPlaybackReferenceFromSeconds(data.currentSong.UseStartTime(this));
            startPlayBackOnDownload = true;
            PublishSharedState(true, false, true);
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
            PublishSharedState(true);
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
        public void RequestSetUnderglowEnabled(bool enabled, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient || !Instance.SyncVisuals.Value)
            {
                return;
            }

            SetUnderglowEnabledLocal(enabled);
        }

        [PunRPC]
        public void RequestSetVisualizerEnabled(bool enabled, int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient || !Instance.SyncVisuals.Value)
            {
                return;
            }

            SetVisualizerEnabledLocal(enabled);
        }

        [PunRPC]
        public void RequestDismissQueue(int requesterId)
        {
            if (!PhotonNetwork.IsMasterClient || Instance.MasterClientDismissQueue.Value && PhotonNetwork.MasterClient.ActorNumber != requesterId)
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
            if (data.currentSong?.ClipLoaded() != true)
            {
                return;
            }

            ApplySharedPlaybackState();
        }

        public double EstimateDownloadTimeSeconds(double durationSeconds, int downloadSpeedMbps)
        {
            int bitrate = DownloadHelper.GetBitrateKbps(ApplyQualityToDownloads ? 4 : AudioPlayer.GetQuality());

            double sizeBytes = (bitrate * 1000.0 / 8.0) * durationSeconds;

            // speed in Mb/s converted to bytes/s
            double speedBytesPerSec = Math.Max(1, downloadSpeedMbps) / 8.0 * 1024 * 1024;

            const int overhead = 5; // 5 seconds for connection, latency, etc.
            double downloadTime = sizeBytes / speedBytesPerSec * 2.5; // factor and overhead
            double processingTime = durationSeconds * 0.005; // FFmpeg estimate
            const double overheadMultiplier = 1.3;

            return (overhead + downloadTime + processingTime) * overheadMultiplier;
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
                    nameof(BaseListener),
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
            data.playbackTime = (int)Math.Round(audioPlayer.GetTime());
        }

        private void OnDestroy()
        {
            Instance.data.GetAllBoomboxes().Remove(this);
            Destroy(GetComponent<BoomboxUI>());
            if (visualizer != null)
                Destroy(visualizer);
            Destroy(gameObject.GetComponent<VisualEffects>());
            Destroy(audioPlayer);
            Destroy(downloadHelper);
            Destroy(gameObject.GetComponent<BoomboxController>());
            photonView.RefreshRpcMonoBehaviourCache();

            /*
            if (photonView != null && PhotonNetwork.IsConnected && PhotonNetwork.CurrentRoom != null && PhotonNetwork.IsMasterClient)
            {
                PhotonHashtable propertiesToRemove = new PhotonHashtable
                {
                    { GetPropertyKey("propVersion"), null },
                    { GetPropertyKey("IDKey"), null },
                    { GetPropertyKey("isPlaying"), null },
                    { GetPropertyKey("pendingPlaybackStart"), null },
                    { GetPropertyKey("absVolume"), null },
                    { GetPropertyKey("loopQueue"), null },
                    { GetPropertyKey("underglow"), null },
                    { GetPropertyKey("visualizer"), null },
                    { GetPropertyKey("currentSongIndex"), null },
                    { GetPropertyKey("playbackStartTimestamp"), null },
                    { GetPropertyKey("queue"), null }
                };

                PhotonNetwork.CurrentRoom.SetCustomProperties(propertiesToRemove);
            }
            */
        }

        public void ResetData()
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            Logger.LogInfo($"Resetting Boombox {photonView.ViewID}");

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

            PublishSharedState(true, true, true);
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

            /* total playback time in seconds */
            public int playbackTime = 0;
            /* relative playback time in milliseconds */
            public int playbackStartTimestamp = 0;
        }

        public class AudioEntry
        {
            public string Title;
            public string Url;
            public double Duration = -1;
            public int StartTime = 0;

            public AudioEntry(string url, SongInfo info)
            {
                Url = url;
                Title = info.title;
                Duration = info.duration;
            }

            private int PeekStartTime(Boombox boombox)
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

            public bool ClipLoaded()
            {
                return DownloadHelper.downloadedClips.ContainsKey(Url) && DownloadHelper.downloadedClips[Url] != null;
            }
        }
    }
}
