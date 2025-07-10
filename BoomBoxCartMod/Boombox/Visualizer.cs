using UnityEngine;

namespace BoomBoxCartMod
{
    public class Visualizer : MonoBehaviour
    {
        public AudioSource audioSource;
        public int numBars = 16;
        public int spectrumSize = 128;
        public float radius = 0.7f;
        public float barWidth = 0.1f;
        public float barMaxHeight = 1.5f;
        public float barMinHeight = 0.15f;
        public float heightMultiplier = 24f;
        private float[] spectrum;
        private Transform[] bars;

        private void Start()
        {
            if (audioSource == null)
                audioSource = GetComponent<AudioSource>();
            spectrum = new float[spectrumSize];
            bars = new Transform[numBars];

            for (int i = 0; i < numBars; i++)
            {
                GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bar.transform.SetParent(transform);
                float angle = Mathf.Lerp(-Mathf.PI / 2, Mathf.PI / 2, (float)i / (numBars - 1));
                Vector3 pos = new Vector3(Mathf.Sin(angle) * radius, 0.5f, Mathf.Cos(angle) * radius + 0.7f);
                bar.transform.localPosition = pos;
                bar.transform.localScale = new Vector3(barWidth, barMinHeight, barWidth);
                bar.GetComponent<Renderer>().material.color = Color.HSVToRGB((float)i / numBars, 1f, 1f);
                Destroy(bar.GetComponent<Collider>());
                bars[i] = bar.transform;
            }
        }

        private void Update()
        {
            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.GetSpectrumData(spectrum, 0, FFTWindow.Blackman);

                for (int i = 0; i < numBars; i++)
                {
                    // Logarithmische Gruppierung
                    int logIndexStart = (int)Mathf.Pow(spectrumSize, (float)i / numBars);
                    int logIndexEnd = (int)Mathf.Pow(spectrumSize, (float)(i + 1) / numBars);
                    logIndexEnd = Mathf.Clamp(logIndexEnd, logIndexStart + 1, spectrumSize);

                    float avg = 0f;
                    for (int j = logIndexStart; j < logIndexEnd; j++)
                        avg += spectrum[j];
                    avg /= (logIndexEnd - logIndexStart);

                    // Verstärkung und "weichere" Skalierung
                    float value = Mathf.Clamp(Mathf.Pow(avg * heightMultiplier, 0.5f), barMinHeight, barMaxHeight);
                    Vector3 scale = bars[i].localScale;
                    scale.y = value;
                    bars[i].localScale = scale;
                }
            }
            else
            {
                for (int i = 0; i < numBars; i++)
                {
                    Vector3 scale = bars[i].localScale;
                    scale.y = barMinHeight;
                    bars[i].localScale = scale;
                }
            }
        }

        private void OnDestroy()
        {
            // Alle erzeugten Balken zerstören
            if (bars != null)
            {
                foreach (var bar in bars)
                {
                    if (bar != null)
                        Destroy(bar.gameObject);
                }
            }
        }
    }
}