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

namespace BoomBoxCartMod
{
	public class Boombox : MonoBehaviourPunCallbacks
	{
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		public PhotonView photonView;
		public AudioSource audioSource;

		public float maxVolumeLimit = 0.8f; // make the range smaller than 0 to 1.0 volume!
		float minDistance = 3f;
		float maxDistanceBase = 10f;
		float maxDistanceAddition = 20f;

        private Queue<string> downloadJobQueue = new Queue<string>();
        private bool isProcessingQueue = false;
        private static string currentRequestId = null;
        private static string currentDownloadUrl = null;
        private Coroutine processingCoroutine; // Should only be used by master client

        public AudioEntry currentSong = null;
        public List<AudioEntry> playbackQueue = new List<AudioEntry>();
        private bool isAwaitingSyncPlayback = false;
		public bool isPlaying = false;
		private bool isTimeoutRecovery = false;

		private AudioLowPassFilter lowPassFilter;
		public static int qualityLevel = 4; // 0 lowest, 4 highest

		// caches the AudioClips in memory using the URL as the key.
		private static Dictionary<string, AudioClip> downloadedClips = new Dictionary<string, AudioClip>();

		// cache for song titles
		private static Dictionary<string, string> songTitles = new Dictionary<string, string>();

		// tracks players ready and errors during/after download phase
		private static Dictionary<string, HashSet<int>> downloadsReady = new Dictionary<string, HashSet<int>>();
		private static Dictionary<string, HashSet<int>> downloadErrors = new Dictionary<string, HashSet<int>>();

		private const float DOWNLOAD_TIMEOUT = 40f; // 40 seconds timeout for downloads
		private Dictionary<string, Coroutine> timeoutCoroutines = new Dictionary<string, Coroutine>();

		// all valid URL's to donwload audio from
		private static readonly Regex[] supportedVideoUrlRegexes = new[]
		{
		// YouTube TODO:/Youtube Music URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www|m)\.)?((?:youtube(?:-nocookie)?\.com|youtu\.be))(\/(?:[\w\-]+\?v=|embed\/|live\/|v\/)?)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    
		// RuTube URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www)?\.?)(rutube\.ru)(\/video\/)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    
		// Yandex Music URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www)?\.?)(music\.yandex\.ru)(\/album\/\d+\/track\/)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    
		// Bilibili URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www|m)\.)?(bilibili\.com)(\/video\/)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),

		// SoundCloud URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www|m)\.)?(soundcloud\.com|snd\.sc)\/([\w\-]+\/[\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)
		};

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
			lowPassFilter = gameObject.AddComponent<AudioLowPassFilter>();
			lowPassFilter.enabled = false;

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
				var vis = gameObject.AddComponent<Visualizer>();
				vis.audioSource = audioSource; // Explicitly set the audiosource
			}

			Logger.LogInfo($"Boombox initialized on this cart. AudioSource: {audioSource}, PhotonView: {photonView}");
		}

		private void Update()
		{
			// try to prevent double playing for the player(s) who downloaded song, but waiting for slowest to sync w
			if (isAwaitingSyncPlayback && audioSource.isPlaying)
			{
				CleanupCurrentPlayback();
				//Logger.LogInfo("Stopped premature audio playback while waiting for sync");
			}

			if (isPlaying && MonstersCanHearMusic && EnemyDirector.instance != null)
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

			if (isPlaying && !audioSource.isPlaying && PhotonNetwork.IsMasterClient) // Handle next song
            {
				int currentIndex = GetCurrentSongIndex();

				if (currentIndex == -1) // Current song not found in queue
				{
                    photonView.RPC("SyncPlayback", RpcTarget.All, -1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                }
                else if (currentIndex + 1 >= playbackQueue.Count) // Current song is at end of queue, stop playback or loop to the start if loopQueue is enabled
				{
					if (!loopQueue)
					{
                        photonView.RPC("SyncPlayback", RpcTarget.All, -1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                    }
                    else
					{
                        photonView.RPC("SyncPlayback", RpcTarget.All, 0, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                    }
                    return;
				}
				else // Middle of queue, start playing the next song
				{
                    photonView.RPC("SyncPlayback", RpcTarget.All, currentIndex + 1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                }
            }
		}

		private void OnDestroy()
		{
			foreach (var coroutine in timeoutCoroutines.Values)
			{
				if (coroutine != null)
				{
					StopCoroutine(coroutine);
				}
			}
			timeoutCoroutines.Clear();
		}

		public static long GetCurrentTimeMilliseconds()
		{
			return DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        }

		private float monsterAttractTimer = 0f;
		private float monsterAttractInterval = 1.0f; // every second

		public static string GetSongTitle(string url)
		{
			if (songTitles.ContainsKey(url))
			{
				return songTitles[url];
			}
			return null;
		}

		public int GetCurrentSongIndex()
		{
			if (currentSong == null)
			{
				return -1;
			}
			return playbackQueue.IndexOf(currentSong);
		}

        private void CleanupCurrentPlayback()
        {
            if (audioSource.isPlaying)
            {
                audioSource.Stop();
                isPlaying = false;
            }

            audioSource.clip = null;
        }

        public void StartPlayBack()
		{
            AudioClip clip = currentSong?.GetAudioClip();
			
			if (clip == null)
			{
                Logger.LogError("Clip not found for current song");
                CleanupCurrentPlayback();
                return;
			}

            if (audioSource.clip != null && audioSource.clip == clip)
            {
				audioSource.Play();
				isPlaying = true;
				return;
            }

			audioSource.Stop(); // Probably unnescessary
            audioSource.clip = clip;
			audioSource.loop = false; // Handled in Update() since we are using a queue
            SetQuality(qualityLevel);
            UpdateAudioRangeBasedOnVolume(audioSource.volume);
            audioSource.Play();
            isPlaying = true;
        }

		public void PausePlayBack()
		{
			audioSource.Pause();
			isPlaying = false;
		}

        //(long)(Boombox.GetCurrentTimeMilliseconds() - Math.Round(timeInSeconds * 1000f)) // Need to round
        public void SetPlaybackTime(long startTimeMillis)
		{
            audioSource.time = Math.Max(0f, (GetCurrentTimeMilliseconds() - startTimeMillis)) / 1000f;
        }

		public bool IsProcessingQueue()
		{
			return isProcessingQueue;
		}

		public string GetCurrentDownloadUrl()
		{
			return currentDownloadUrl;
		}

		public static bool IsValidVideoUrl(string url)
		{
			return !string.IsNullOrWhiteSpace(url) && supportedVideoUrlRegexes.Any(regex => regex.IsMatch(url));
		}

		private void DownloadQueue(int startIndex)
		{
			if (startIndex < 0 || startIndex >= playbackQueue.Count)
			{
				startIndex = 0;
			}

            for (int i = startIndex; i < playbackQueue.Count; i++) // Songs in the queue after the current song
            {
                downloadJobQueue.Enqueue(playbackQueue.ElementAt(i).Url);
            }
            for (int i = 0; i < startIndex; i++) // Songs in the queue from 0 to the current song
            {
                downloadJobQueue.Enqueue(playbackQueue.ElementAt(i).Url);
            }

            if (!isProcessingQueue)
            {
                processingCoroutine = StartCoroutine(ProcessDownloadQueue());
            }
        }

		[PunRPC]
		public async void RequestSong(string url, int requesterId)
		{
            //Logger.LogInfo($"RequestSong RPC received: url={url}, requesterId={requesterId}");

            /* TODO: Add an option to allow / prohibit duplicates in the queue
			if (!playbackQueue.Any(entry => entry.Url == url))
			{ }
			*/

            bool ownRequest = requesterId == PhotonNetwork.LocalPlayer.ActorNumber;

            if (!IsValidVideoUrl(url))
			{
				Logger.LogError($"Invalid Video URL: {url}");
				if (ownRequest)
				{
					UpdateUIStatus("Error: Invalid Video URL.");
				}
				return;
			}

            AudioEntry song = new AudioEntry(songTitles.ContainsKey(url) ? songTitles[url] : "Unknown Title", url);
            playbackQueue.Add(song);

			bool isCurrentSong = false;
			if (currentSong == null) // Start playback
			{
				isCurrentSong = true;
				currentSong = song;
                isAwaitingSyncPlayback = true;
            }


            if (!PhotonNetwork.IsMasterClient)
				return;

			downloadJobQueue.Enqueue(url);

			if (!isProcessingQueue)
			{
				processingCoroutine = StartCoroutine(ProcessDownloadQueue());
			}

			return;
		}

        [PunRPC]
		public void NotifyPlayersOfErrors(string message)
		{
			Logger.LogWarning(message);
			UpdateUIStatus(message);
		}

		[PunRPC]
		public void ReportDownloadError(int actorNumber, string url, string errorMessage)
		{
			Logger.LogError($"Player {actorNumber} reported download error for {url}: {errorMessage}");

			if (!downloadErrors.ContainsKey(url))
				downloadErrors[url] = new HashSet<int>();

			downloadErrors[url].Add(actorNumber);

			if (actorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
			{
				UpdateUIStatus($"Error: {errorMessage}");
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
            }

            // Queue is empty, stop the processor.
            isProcessingQueue = false;
			currentDownloadUrl = null;
            Logger.LogInfo("Master Client Download Queue Processor finished.");
        }

        private IEnumerator MasterClientInitiateSync(string url)
        {
            // Check if the song is still in the local playbackQueue (it might have been removed by a user)
            if (!playbackQueue.Any(entry => entry.Url == url))
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
                if (downloadsReady[url].Count == PhotonNetwork.PlayerList.Length) // All players already have the song downloaded
                {
					if (!isPlaying && !audioSource.isPlaying && isAwaitingSyncPlayback && url == currentSong?.Url)
					{
						isAwaitingSyncPlayback = false;
						photonView.RPC("PlayPausePlayback", RpcTarget.All, true, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber); // Should not do anything if there is already a song playing
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
            photonView.RPC("StartDownloadAndSync", RpcTarget.All, url, PhotonNetwork.LocalPlayer.ActorNumber);

            // Start timeout coroutine
            timeoutCoroutines[requestId] = StartCoroutine(DownloadTimeoutCoroutine(requestId, url));

            // Yield until the consensus is reached
            yield return StartCoroutine(WaitForPlayersReadyOrFailed(url));

            // --- Cleanup and Final Sync ---

            if (timeoutCoroutines.TryGetValue(requestId, out Coroutine coroutine) && coroutine != null)
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

			if (isAwaitingSyncPlayback && url == currentSong?.Url)
			{
				isAwaitingSyncPlayback = false;
				photonView.RPC("PlayPausePlayback", RpcTarget.All, true, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
			}
        }

        private IEnumerator DownloadTimeoutCoroutine(string requestId, string url)
		{
			yield return new WaitForSeconds(DOWNLOAD_TIMEOUT);

			if (currentRequestId == requestId && isProcessingQueue)
			{
				Logger.LogWarning($"Download timeout for url: {url}");

				if (PhotonNetwork.IsMasterClient)
				{
					Logger.LogInfo("Master client initiating timeout recovery");
					isTimeoutRecovery = true;

					foreach (var player in PhotonNetwork.PlayerList)
					{
						if (!downloadsReady[url].Contains(player.ActorNumber))
						{
							if (!downloadErrors.ContainsKey(url))
								downloadErrors[url] = new HashSet<int>();

							downloadErrors[url].Add(player.ActorNumber);
							Logger.LogWarning($"Player {player.ActorNumber} timed out during download");
						}
					}

					if (downloadsReady.ContainsKey(url) && downloadsReady[url].Count > 0)
					{
						string timeoutMessage = $"Some players timed out. Continuing playback for {downloadsReady[url].Count} players.";
						photonView.RPC("NotifyPlayersOfErrors", RpcTarget.All, timeoutMessage);

						// initiate playback with the master client as requester to unblock the system
						if (currentSong?.Url == url)
						{
							photonView.RPC("PlayPausePlayback", RpcTarget.All, true, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
						}
                    }
                    else
					{
						photonView.RPC("NotifyPlayersOfErrors", RpcTarget.All, "Download timed out for all players.");
					}

					currentDownloadUrl = null;
					currentRequestId = null;
					isTimeoutRecovery = false;
				}
			}

			timeoutCoroutines.Remove(requestId);
		}

		[PunRPC]
		public async void StartDownloadAndSync(string url, int requesterId)
		{
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
                photonView.RPC("ReportDownloadComplete", RpcTarget.All, url, PhotonNetwork.LocalPlayer.ActorNumber);
                return;
			}

			if (await StartAudioDownload(this, url))
			{
                photonView.RPC("ReportDownloadComplete", RpcTarget.All, url, PhotonNetwork.LocalPlayer.ActorNumber);
            }
        }

        [PunRPC]
		public void SetSongTitle(string url, string title)
		{
			// Cache title
			songTitles[url] = title;

			if (currentSong != null && currentSong.Url == url)
			{
				currentSong.Title = title;
				if (currentSong.GetAudioClip() != null)
				{
					UpdateUIStatus($"Now playing: {title}");
				}
				else
				{
                    UpdateUIStatus($"Loading: {title}");
                }
            }
		}

		[PunRPC]
		public void ReportDownloadComplete(string url, int actorNumber)
		{
			if (!PhotonNetwork.IsMasterClient)
				return;

			if (!downloadsReady.ContainsKey(url))
				downloadsReady[url] = new HashSet<int>();

			if (audioSource.isPlaying && isPlaying) // Handle e.g. late join
			{
				PhotonNetwork.CurrentRoom.Players.TryGetValue(actorNumber, out Player targetPlayer);
                if (!downloadsReady[url].Contains(actorNumber) && targetPlayer != null)
				{
                    photonView.RPC("PlayPausePlayback", targetPlayer, true, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                }
            }

            downloadsReady[url].Add(actorNumber);
            //Logger.LogInfo($"Player {actorNumber} reported ready for url: {url}. Total ready: {downloadsReady[url].Count}");
        }

        private IEnumerator WaitForPlayersReadyOrFailed(string url)
		{
			int totalPlayers = PhotonNetwork.PlayerList.Length;
			int readyCount = 0;
			int errorCount = 0;

			float waitTime = 0.1f;

            float? partialConsensusStartTime = null;
            const float timeoutThreshold = 5.0f;


            // Wait until either all players are accounted for (ready + error = total) 
            // or we have at least one player ready and some have errors
            while (true)
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
					timeOutReached = (Time.time - partialConsensusStartTime.Value) >= timeoutThreshold;
                }


                if (allAccountedFor || timeOutReached)
				{
					break;
				}

				yield return new WaitForSeconds(waitTime);
			}

			Logger.LogInfo($"n. Ready: {readyCount}, Errors: {errorCount}, Total: {totalPlayers} for url: {url}");
		}

		[PunRPC]
        public void SyncQueue(int currentIndex, List<AudioEntry> queue, long currentSongTimeLeft, int requesterId) // Syncs the queue, but presumes no changes to the current song were made
        {
            if (requesterId != PhotonNetwork.MasterClient.ActorNumber) // Should not be necessary, but why not
				return;

            //Logger.LogInfo($"SyncQueue RPC received: currentIndex={currentIndex}, queue={queue}");

			playbackQueue = queue;
			currentSong = playbackQueue.ElementAt(currentIndex);

			foreach (AudioEntry song in playbackQueue)
			{
				string title = song.Title;

                if (!songTitles.ContainsKey(title))
				{
					songTitles[title] = title;
				}
			}

            /* TODO: Add a way to download songs independantly from master client for late joins
            int downloadStartIndex = GetCurrentSongIndex();
            if (currentSongTimeLeft > 10000) // Do not bother trying to download the current song, if it has less than 10 seconds left to play
            {
                downloadStartIndex += 1;
            }

            downloadStartIndex %= playbackQueue.Count;
			*/
        }


        [PunRPC]
        public void DismissQueue(int requesterId)
        {
			if (requesterId != PhotonNetwork.MasterClient.ActorNumber) // Only allow the Host to dismiss the queue  -- TODO: Add option
				return;

            if (audioSource.isPlaying)
            {
                CleanupCurrentPlayback();
                //Logger.LogInfo($"Playback stopped by player {requesterId}");
                UpdateUIStatus("Ready to play music! Enter a Video URL");
            }
			playbackQueue.Clear();
			currentSong = null;

			if (PhotonNetwork.IsMasterClient)
			{
				downloadJobQueue.Clear();
				ForceCancelDownload();
			}
        }

        [PunRPC]
        public void MoveQueueItem(int index, int newIndex, int requesterId) // Moves an item in the queue
        {
            //Logger.LogInfo($"MoveQueueItem RPC received: index={index}, newIndex={newIndex}");

            if (index < 0 || index >= playbackQueue.Count || newIndex < 0 || newIndex >= playbackQueue.Count || index == newIndex)
			{
                Logger.LogError($"MoveQueueItem RPC received with invalid indices: {index}, {newIndex}, queue count: {playbackQueue.Count}");
                return;
			}

			AudioEntry tempSong = playbackQueue[index];
            playbackQueue.RemoveAt(index);
            playbackQueue.Insert(newIndex, tempSong);

			if (!PhotonNetwork.IsMasterClient)
				return;

            downloadJobQueue.Clear();
            DownloadQueue(GetCurrentSongIndex()); // Not ideal, but best i can be asked to do for now. Will check if songs have been previously downloaded, before trying to download them anyway.
        }

        [PunRPC]
        public void RemoveQueueItem(int index, int requesterId) // Removes an item from the queue
        {
            if (index < 0 || index > playbackQueue.Count)
            {
                Logger.LogError($"RemoveQueueItem RPC received with wrong index: index={index}, songCount={playbackQueue.Count}, requesterId={requesterId}");
                return;
            }
            //Logger.LogInfo($"RemoveQueueItem RPC received: index={index}");

			int currentIndex = GetCurrentSongIndex();
			playbackQueue.RemoveAt(index);

            if (index == currentIndex) // Should never happen, but just to make sure
			{
				CleanupCurrentPlayback();
				currentSong = null;

				if (PhotonNetwork.IsMasterClient)
				{
					if (playbackQueue.Count > 0)
					{
						index %= playbackQueue.Count; // currentSongIndex == index
						if (currentIndex > 0 || loopQueue)
						{
							photonView.RPC("SyncPlayback", RpcTarget.All, currentIndex, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
							return;
						}
					}

                    photonView.RPC("SyncPlayback", RpcTarget.All, -1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                }
            }
        }

        [PunRPC]
		public async void SyncPlayback(int newSongIndex, long startTime, int requesterId) // Syncs the currently playing song, but presumes no changes to the queue were made
		{
			if (newSongIndex == -1) // Used to stop playback
			{
				isAwaitingSyncPlayback = false;
				currentSong = null;
				CleanupCurrentPlayback();
				return;
			}
            else if (newSongIndex >= playbackQueue.Count) // Invalid
            {
                Logger.LogError($"SyncPlayback RPC received with wrong index: newSongIndex={newSongIndex}, songCount={playbackQueue.Count}, startTime={startTime}, requesterId={requesterId}");
                return;
            }

            //Logger.LogInfo($"SyncPlayback RPC received: newSongIndex={newSongIndex}, startTime={startTime}, requesterId={requesterId}");


            if (GetCurrentSongIndex() == newSongIndex) {
				if (!isPlaying || !audioSource.isPlaying) // TODO: Possibly do not start playing the song when the time of the song is changed, as this should be handled by PlayPausePlayback
				{
					StartPlayBack();
				}
				SetPlaybackTime(startTime);
				return;
			}
			else if (isPlaying || audioSource.isPlaying) {
				CleanupCurrentPlayback();
			}
			
			AudioEntry oldSong = currentSong;
			currentSong = playbackQueue.ElementAt(newSongIndex);

            if (!PhotonNetwork.IsMasterClient) // Handle starting to play a new song from here
				return;

			if (currentSong?.Url != null && oldSong?.Url != currentSong.Url) // The download process will play the song once it is finished
			{
				// TODO: add proper logic to where the song should be added, instead of just iterating over the whole queue
				downloadJobQueue.Clear();
                isAwaitingSyncPlayback = true;
                DownloadQueue(newSongIndex);
			}
			else 
			{
				photonView.RPC("PlayPausePlayback", RpcTarget.All, true, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
			}
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

			float actualVolume = volume * maxVolumeLimit;
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

        [PunRPC]
		public void PlayPausePlayback(bool startPlaying, long startTime, int requesterId)
		{
			if (playbackQueue.Count == 0)
			{
				return;
			}

			if (startPlaying != isPlaying)
			{
				string playPauseText;
				if (startPlaying)
				{
					if (currentSong == null)
					{
						if (PhotonNetwork.IsMasterClient)
						{
							photonView.RPC("SyncPlayback", RpcTarget.All, 0, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
						}
						return;
					}
					StartPlayBack();
					SetPlaybackTime(startTime);
					playPauseText = "Started";
				}
				else
				{
					PausePlayBack();
					playPauseText = "Paused";
				}
				Logger.LogInfo($"Playback {playPauseText} by player {requesterId}");
				UpdateUIStatus($"{playPauseText} Playback");
			}

            UpdateUIStatus($"Now playing: {(currentSong?.Url == null ? "Unkown" : currentSong?.Title)}");

            if (MonstersCanHearMusic && EnemyDirector.instance != null && currentSong != null && transform?.position != null)
            {
                EnemyDirector.instance.SetInvestigate(transform.position, 5f);
            }
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

				photonView.RPC("ReportDownloadError", RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber, urlToReport, "Download cancelled.");

                Logger.LogInfo("Download was force cancelled by user.");
			}
		}

		private void UpdateUIStatus(string message)
		{
			BoomboxUI ui = GetComponent<BoomboxUI>();
			if (ui != null && ui.IsUIVisible())
			{
				ui.UpdateStatus(message);
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

		public override void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
		{
			// no idea if this works, but ancitipating a late join mod

			base.OnPlayerEnteredRoom(newPlayer);

			if (PhotonNetwork.IsMasterClient && isPlaying && currentSong != null && currentSong.Url != null)
			{
				Logger.LogInfo($"New player {newPlayer.ActorNumber} joined - syncing current playback state");

                photonView.RPC("SyncQueue", newPlayer, GetCurrentSongIndex(), playbackQueue, (long)Math.Round((audioSource.clip.length - audioSource.time) * 1000f), PhotonNetwork.LocalPlayer.ActorNumber); // Syncs and downloads queue
				//photonView.RPC("UpdateQuality", newPlayer, qualityLevel, PhotonNetwork.LocalPlayer.ActorNumber);
                photonView.RPC("UpdateLooping", newPlayer, LoopQueue, PhotonNetwork.LocalPlayer.ActorNumber);
                //photonView.RPC("UpdateVolume", newPlayer, Buffered, normalizedVolume, PhotonNetwork.LocalPlayer.ActorNumber);
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

        private bool loopQueue = false;
		public bool LoopQueue
		{
    		get => loopQueue;
    		set => loopQueue = value;
		}

		public static async Task<AudioClip> GetAudioClipAsync(string filePath)
		{
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
				www.timeout = (int) DOWNLOAD_TIMEOUT;

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

				// MOVED to YouTubeDL.CleanUp()

				return clip;
			}
		}


        public async Task<bool> StartAudioDownload(Boombox boombox, string url)
        {
            if (!downloadedClips.ContainsKey(url))
            {
                try
                {
                    var title = await YoutubeDL.DownloadAudioTitleAsync(url);

                    songTitles[url] = title;
                    boombox.photonView.RPC("SetSongTitle", RpcTarget.All, url, title);
                    //Logger.LogInfo($"Set song title for url: {url} to {title}");

                    var filePath = await YoutubeDL.DownloadAudioAsync(url, title);

                    if (currentSong?.Url == url)
                    {
                        boombox.UpdateUIStatus($"Processing audio: {title}");
                    }

                    AudioClip clip = await GetAudioClipAsync(filePath);

                    downloadedClips[url] = clip;
                    Logger.LogInfo($"Downloaded and cached clip for video: {title}");
                }
                catch (Exception ex)
                {
                    //Logger.LogError($"Failed to download audio: {ex.Message}");

                    if (currentSong?.Url == url)
                    {
                        boombox.UpdateUIStatus($"Error: {ex.Message}");
                    }

                    // Report download error to other players
                    boombox.photonView.RPC("ReportDownloadError", RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber, url, ex.Message);

                    return false;
                }
            }
            else
            {
                //Logger.LogInfo($"Clip already cached for url: {Url}");
            }


            foreach (AudioEntry song in boombox.playbackQueue.FindAll(entry => entry.Url == url))
			{
				if (songTitles.ContainsKey(url))
					song.Title = songTitles[url];
			}

            return true;
        }


        public class AudioEntry
        {
            public string Title;
            public string Url;

            public AudioEntry(string title, string url)
            {
                Title = title;
                Url = url;
            }

			public AudioClip GetAudioClip()
			{
				return downloadedClips.ContainsKey(Url) ? downloadedClips[Url] : null;
            }
        }
    }
}
