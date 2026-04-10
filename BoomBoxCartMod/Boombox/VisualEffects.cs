using UnityEngine;

namespace BoomBoxCartMod
{
    public class VisualEffects : MonoBehaviour
    {
        private static BoomBoxCartMod Instance => BoomBoxCartMod.instance;

        private Light frontLight;
        private Light backLight;
        private AudioSource audioSource;

        // Baseline hue-cycle speed when no audio is driving the underglow.
        private const float BaseRgbSpeed = 0.18f;

        // Base point-light radius before any beat-driven expansion.
        private const float BaseLightRange = 6f;

        // Maximum multiplier applied to the light radius at peak bass intensity.
        private const float MaxLightRangeMultiplier = 1.3f;

        private const float BaseIntensity = 1f;

        private const float MaxLightIntensityMultiplier = 2f;

        // Prevents very small non-zero playback volumes from over-amplifying analysis noise.
        private const float MinSpectrumNormalizationVolume = 0.1f;

        // Caps how much the beat analysis can accelerate the hue cycle.
        private const float MaxBassSpeedBoost = 0.85f;

        // How quickly the effect reacts when bass intensity rises.
        private const float BassResponse = 20f;

        // How quickly the effect settles back down after the beat fades.
        private const float BassRelease = 4f;

        // FFT sample count used for spectrum analysis. Higher resolution makes low-end detection more reliable.
        private const int SpectrumSize = 512;

        // Frequency span used for the isolated bass response.
        private const float BassMaxFrequency = 220f;

        // Frequency span used for the broader low-end response when bass bias is low.
        private const float LowEndMaxFrequency = 900f;

        // Upper frequency bound used when rejecting non-bass energy from the isolated bass response.
        private const float NonBassMaxFrequency = 2500f;

        // Adds some peak sensitivity so kick drums and bass hits read more clearly than a plain average.
        private const float PeakEmphasis = 0.65f;

        // Lower clamp used when converting FFT data into visualizer-style values.
        private const float AnalysisBarMin = 0.15f;

        // Upper clamp used when converting FFT data into visualizer-style values.
        private const float AnalysisBarMax = 1.5f;

        // Scales raw FFT amplitudes before the visualizer-style normalization step.
        private const float AnalysisHeightMultiplier = 24f;

        // How strongly non-bass energy suppresses the focused bass response.
        private const float NonBassLeakRejection = 0.8f;
        private readonly float[] spectrum = new float[SpectrumSize];
        private float hueProgress = 0f;
        private float bassIntensity = 0f;
        private bool lightsOn = false;

        private void Start()
        {
            audioSource = GetComponent<AudioSource>();

            // Front Light
            GameObject front = new GameObject("BoomboxFrontLight");
            front.transform.SetParent(transform);
            front.transform.localPosition = new Vector3(0f, 0f, 1f);
            frontLight = front.AddComponent<Light>();
            frontLight.type = LightType.Point;
            frontLight.range = BaseLightRange;
            frontLight.intensity = BaseIntensity;
            frontLight.enabled = lightsOn;

            // Back Light
            GameObject back = new GameObject("BoomboxBackLight");
            back.transform.SetParent(transform);
            back.transform.localPosition = new Vector3(0f, 0f, -1f);
            backLight = back.AddComponent<Light>();
            backLight.type = LightType.Point;
            backLight.range = BaseLightRange;
            backLight.intensity = BaseIntensity;
            backLight.enabled = lightsOn;
        }

        private void Update()
        {
            if (lightsOn)
            {
                float speed = BaseRgbSpeed + GetBassSpeedBoost();
                float lightRange = BaseLightRange * Mathf.Lerp(1f, MaxLightRangeMultiplier, bassIntensity);
                float lightIntensity = BaseIntensity * Mathf.Lerp(1f, MaxLightIntensityMultiplier, bassIntensity);

                hueProgress = Mathf.Repeat(hueProgress + Time.deltaTime * speed, 1f);
                Color rgb = Color.HSVToRGB(hueProgress, 1f, 1f);
                if (frontLight != null)
                {
                    frontLight.color = rgb;
                    frontLight.range = lightRange;
                    frontLight.intensity = lightIntensity;
                }

                if (backLight != null)
                {
                    backLight.color = rgb;
                    backLight.range = lightRange;
                    backLight.intensity = lightIntensity;
                }
            }
        }

        private float GetBassSpeedBoost()
        {
            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
            }

            float outputVolume = audioSource != null ? Mathf.Clamp01(audioSource.volume) : 0f;

            float targetBassIntensity = 0f;

            if (audioSource != null && audioSource.isPlaying && outputVolume > 0f)
            {
                audioSource.GetSpectrumData(spectrum, 0, FFTWindow.Blackman);
                float normalizedVolume = Mathf.Max(outputVolume, MinSpectrumNormalizationVolume);
                float spectrumNormalizationScale = 1f / normalizedVolume;

                float broadIntensity = GetFrequencyRangeIntensity(20f, LowEndMaxFrequency, spectrumNormalizationScale, PeakEmphasis * 0.35f);
                float bassFrequencyIntensity = GetFrequencyRangeIntensity(20f, BassMaxFrequency, spectrumNormalizationScale, PeakEmphasis);
                float nonBassIntensity = GetFrequencyRangeIntensity(BassMaxFrequency, NonBassMaxFrequency, spectrumNormalizationScale, PeakEmphasis * 0.15f);
                float isolatedBassIntensity = Mathf.Clamp01(bassFrequencyIntensity - (nonBassIntensity * NonBassLeakRejection));

                targetBassIntensity = Mathf.Lerp(broadIntensity, isolatedBassIntensity, Instance.UnderglowBassBias.Value);

                bassIntensity = Mathf.Lerp(bassIntensity, targetBassIntensity, Time.deltaTime * BassResponse);
            }
            else
            {
                bassIntensity = Mathf.Lerp(bassIntensity, 0f, Time.deltaTime * BassRelease);
            }

            return bassIntensity * MaxBassSpeedBoost * Instance.UnderglowBeatSpeed.Value;
        }

        private float GetFrequencyRangeIntensity(float minFrequency, float maxFrequency, float spectrumNormalizationScale, float peakBlend)
        {
            float binFrequency = (AudioSettings.outputSampleRate * 0.5f) / SpectrumSize;
            int startIndex = Mathf.Clamp(Mathf.FloorToInt(minFrequency / binFrequency), 1, SpectrumSize - 1);
            int endIndex = Mathf.Clamp(Mathf.CeilToInt(maxFrequency / binFrequency), startIndex + 1, SpectrumSize);

            float total = 0f;
            float peak = 0f;
            int sampleCount = 0;

            for (int index = startIndex; index < endIndex; index++)
            {
                float normalizedSample = GetNormalizedSpectrumSample(spectrum[index], spectrumNormalizationScale);
                total += normalizedSample;
                peak = Mathf.Max(peak, normalizedSample);
                sampleCount++;
            }

            if (sampleCount == 0)
            {
                return 0f;
            }

            float average = total / sampleCount;
            return Mathf.Lerp(average, peak, peakBlend);
        }

        private float GetNormalizedSpectrumSample(float sample, float spectrumNormalizationScale)
        {
            float scaledSample = sample * spectrumNormalizationScale;
            float visualizerValue = Mathf.Clamp(Mathf.Pow(scaledSample * AnalysisHeightMultiplier, 0.5f), AnalysisBarMin, AnalysisBarMax);
            return Mathf.InverseLerp(AnalysisBarMin, AnalysisBarMax, visualizerValue);
        }

        public void SetLights(bool on)
        {
            lightsOn = on;
            if (frontLight != null)
            {
                frontLight.enabled = on;
                if (!on)
                {
                    frontLight.range = BaseLightRange;
                    frontLight.intensity = BaseIntensity;
                }
            }

            if (backLight != null)
            {
                backLight.enabled = on;
                if (!on) 
                {
                    backLight.range = BaseLightRange;
                    backLight.intensity = BaseIntensity;
                }
            }
        }

        public bool AreLightsOn()
        {
            return lightsOn;
        }
    }
}