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

namespace BoomBoxCartMod
{
	public class Boombox : MonoBehaviourPunCallbacks
	{
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		public PhotonView photonView;
		public AudioSource audioSource;

		public DownloadHelper downloadHelper = null;

		public float maxVolumeLimit = 0.8f; // make the range smaller than 0 to 1.0 volume!
		float minDistance = 3f;
		float maxDistanceBase = 10f;
		float maxDistanceAddition = 20f;

        public AudioEntry currentSong = null;
        public List<AudioEntry> playbackQueue = new List<AudioEntry>();
        public bool isAwaitingSyncPlayback = false;
		public bool isPlaying = false;

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

			if (!mutePressed && Keyboard.current != null && Keyboard.current[Instance.GlobalMuteKey.Value].wasPressedThisFrame)
			{
				mutePressed = true;
				audioMuted = !audioMuted;
				if (audioSource != null) // Using this here means the audio can only me (un)muted while there is a cart in the lobby
				{
					audioSource.mute = audioMuted;
				}
			}
			else if (mutePressed && (Keyboard.current == null || Keyboard.current[Instance.GlobalMuteKey.Value].wasReleasedThisFrame))
			{
				mutePressed = false;
			}

			if (isPlaying && !audioSource.isPlaying && PhotonNetwork.IsMasterClient) // Handle next song TODO: Fix issues
            {
				int currentIndex = GetCurrentSongIndex();
				CleanupCurrentPlayback();

				if (currentIndex == -1) // Current song not found in queue
				{
                    photonView.RPC("SyncPlayback", RpcTarget.All, -1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                }
                else if (currentIndex + 1 >= playbackQueue.Count) // Current song is at end of queue, stop playback or loop to the start if loopQueue is enabled
				{
					photonView.RPC("SyncPlayback", RpcTarget.All, loopQueue ? 0 : -1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                    return;
				}
				else // Middle of queue, start playing the next song
				{
                    photonView.RPC("SyncPlayback", RpcTarget.All, currentIndex + 1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
                }
            }
		}

		public static long GetCurrentTimeMilliseconds()
		{
			return DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
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
			if (currentSong == null)
			{
				return -1;
			}
			return playbackQueue.IndexOf(currentSong);
		}

        private void CleanupCurrentPlayback()
        {
			if (audioSource.isPlaying)
				audioSource.Stop();
            isPlaying = false;

            audioSource.clip = null;
        }

        public void StartPlayBack() // TODO: Add request to check if a player is using the mod, if the request does not come back, ignore them
		{
            AudioClip clip = currentSong?.GetAudioClip();
			
			if (clip == null)
			{
                Logger.LogError("Clip not found for current song");
                CleanupCurrentPlayback();
                return;
			}

            if (audioSource.clip == clip)
            {
				audioSource.Play();
				isPlaying = true;
				return;
            }

			CleanupCurrentPlayback(); // Probably unnescessary
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

		[PunRPC]
		public async void RequestSong(string url, int seconds, int requesterId)
		{
            //Logger.LogInfo($"RequestSong RPC received: url={url}, requesterId={requesterId}");

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

			downloadHelper.EnqueueDownload(url);
			downloadHelper.StartDownloadJob();

			return;
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

                if (!DownloadHelper.songTitles.ContainsKey(title))
				{
					DownloadHelper.songTitles[title] = title;
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
			if (requesterId != PhotonNetwork.MasterClient.ActorNumber) // Only allow the Host to dismiss the queue
			{
				if (PhotonNetwork.IsMasterClient && !Instance.MasterClientDismissQueue.Value) // Only depends on the host's config setting
				{
                    photonView.RPC("DismissQueue", RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber);
                }
                return;
			}

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
				downloadHelper.DismissDownloadQueue();
				downloadHelper.ForceCancelDownload();
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

            downloadHelper.DismissDownloadQueue();
            downloadHelper.DownloadQueue(GetCurrentSongIndex()); // Not ideal, but best i can be asked to do for now. Will check if songs have been previously downloaded, before trying to download them anyway.
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
						if (currentIndex < playbackQueue.Count || loopQueue) // If the song was not the last in the queue or we want to loop
						{
							photonView.RPC("SyncPlayback", RpcTarget.All, index, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
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

            //Logger.LogInfo($"SyncPlayback RPC received: newSongIndex={newSongIndex}, startTime={startTime}, requesterId={requesterId}, queueSize={playbackQueue.Count}");


            if (GetCurrentSongIndex() == newSongIndex) {
                SetPlaybackTime(startTime);
                if (!isPlaying || !audioSource.isPlaying) // TODO: Possibly do not start playing the song when the time of the song is changed, as this should be handled by PlayPausePlayback
				{
					StartPlayBack();
				}
				return;
			}
			else if (isPlaying || audioSource.isPlaying) { // Changing to new song
				CleanupCurrentPlayback();
			}
			
			AudioEntry oldSong = currentSong;
			currentSong = playbackQueue.ElementAt(newSongIndex);

            if (!PhotonNetwork.IsMasterClient) // Handle starting to play a new song from here
				return;

			if (currentSong?.Url == null) // Should never happen
			{
                photonView.RPC("RemoveQueueItem", RpcTarget.All, newSongIndex, PhotonNetwork.LocalPlayer.ActorNumber);
                return;
			}

			if (currentSong.GetAudioClip() == null) // The download process will play the song once it is finished
			{
				// TODO: add proper logic to where the song should be added, instead of just iterating over the whole queue
				if (currentSong.Url != downloadHelper.GetCurrentDownloadUrl()) 
					downloadHelper.DismissDownloadQueue();
                isAwaitingSyncPlayback = true;
                if (currentSong.Url != downloadHelper.GetCurrentDownloadUrl())
                    downloadHelper.DownloadQueue(newSongIndex);
			}
			else
			{
				photonView.RPC(
					"PlayPausePlayback",
					RpcTarget.All,
					true,
					Boombox.GetCurrentTimeMilliseconds() - currentSong.UseStartTime() * 1000,
					PhotonNetwork.LocalPlayer.ActorNumber
				);
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
							photonView.RPC("SyncPlayback", RpcTarget.All, -1, Boombox.GetCurrentTimeMilliseconds(), PhotonNetwork.LocalPlayer.ActorNumber);
						}
						return;
					}
					SetPlaybackTime(startTime);
					StartPlayBack();
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

		public void UpdateUIStatus(string message)
		{
			BoomboxUI ui = GetComponent<BoomboxUI>();
			if (ui != null && ui.IsUIVisible())
			{
				ui.UpdateStatus(message);
			}
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
			public int UseStartTime()
			{
				int re = StartTime;
				if (Instance.UseTimeStampOnce.Value)
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
