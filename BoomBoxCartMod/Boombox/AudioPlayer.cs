using BepInEx.Logging;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace BoomBoxCartMod
{
    public class AudioPlayer : MonoBehaviourPunCallbacks
    {
        private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
        private static ManualLogSource Logger => Instance.logger;

        private static HashSet<string> urlsInUse = new HashSet<string>(); // Used to detect shared audio files

        public float minDistance = 3f;
        public float maxDistanceBase = 15f;
        public float maxDistanceAddition = 30f;

        private static int qualityLevel = 4; // 0 lowest, 4 highest

        private AudioLowPassFilter lowPassFilter;
        public AudioSource audioSource;

        public string currentUrl;

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

            UpdateAudioRangeBasedOnVolume();
        }

        private void Update()
        {
            if (Instance.baseListener.audioMuted != audioSource.mute)
            {
                audioSource.mute = Instance.baseListener.audioMuted;
            }
        }

        public AudioClip GetClip()
        {
            return audioSource == null ? null : audioSource.clip;
        }

        public void SetVolume(float volume)
        {
            audioSource.volume = volume;
            UpdateAudioRangeBasedOnVolume();
        }

        public void UpdateAudioRangeBasedOnVolume()
        {
            UpdateAudioRangeBasedOnVolume(audioSource.volume);
        }
        public void UpdateAudioRangeBasedOnVolume(float volume)
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

        public void SetClip(string url)
        {
            if (url == null || (url == currentUrl && audioSource.clip != null))
            {
                return;
            }

            if (currentUrl != null)
            {
                urlsInUse.Remove(currentUrl);
            }

            if (audioSource.clip != null && audioSource.clip.name.Contains("_clone_" + photonView.ViewID ))
            {
                Destroy(audioSource.clip);
            }

            DownloadHelper.downloadedClips.TryGetValue(url, out var clip);

            if (clip != null)
            {
                // If this URL is already in use by another AudioSource, clone it to avoid accessing the same audio file from multiple sources
                if (urlsInUse.Contains(url))
                {
                    clip = CloneAudioClip(clip);
                    Logger.LogDebug($"Audio clip for URL {url} is already in use, cloning...");
                }

                audioSource.clip = clip;
                currentUrl = url;
                urlsInUse.Add(url);
            }
        }

        private AudioClip CloneAudioClip(AudioClip source)
        {
            // Manually copy audio samples, not great for performance but shouldn't happen often
            float[] samples = new float[source.samples * source.channels];
            source.GetData(samples, 0);

            AudioClip clone = AudioClip.Create(source.name + "_clone_" + photonView.ViewID, source.samples, source.channels, source.frequency, false);
            clone.SetData(samples, 0);
            return clone;
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
        public static int GetQuality()
        {
            return qualityLevel;
        }

        public float GetTime()
        {
            return audioSource.time;
        }
        public void SetTime(float time)
        {
            // Prevent setting time beyond clip length which can stop audio unexpectedly
            if (audioSource.clip != null && time > audioSource.clip.length)
            {
                time = Mathf.Max(0f, audioSource.clip.length - 0.05f);
                Logger.LogDebug($"SetTime: Clamped time from {audioSource.time} to {time} (clip length: {audioSource.clip.length})");
            }
            audioSource.time = time;
        }

        public bool IsPlaying()
        {
            return audioSource.isPlaying;
        }

        public bool SongReachedEnd()
        {
            return audioSource.clip != null
                && audioSource.time >= Math.Max(0f, audioSource.clip.length - 0.05f);
        }

        public void Play()
        {
            audioSource.Play();
        }

        public void Pause()
        {
            audioSource.Pause();
        }

        public void Stop()
        {
            if (audioSource.isPlaying)
            {
                audioSource.Stop();
            }
            audioSource.time = 0f;

            // Destroy cloned clip to avoid memory leaks
            if (audioSource.clip != null && audioSource.clip.name.Contains("_clone_" + photonView.ViewID))
            {
                Destroy(audioSource.clip);
            }

            audioSource.clip = null;

            // Remove URL from tracking
            if (currentUrl != null)
            {
                urlsInUse.Remove(currentUrl);
                currentUrl = null;
            }
        }


        private void OnDestroy()
        {
            Destroy(lowPassFilter);

            // Destroy cloned clip if present
            if (audioSource.clip != null && audioSource.clip.name.Contains("_clone_" + photonView.ViewID))
            {
                Destroy(audioSource.clip);
            }

            Destroy(audioSource);
            if (currentUrl != null)
            {
                urlsInUse.Remove(currentUrl);
            }
        }
    }
}
