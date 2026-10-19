using UnityEngine;

namespace DeadPines.Audio
{
    /// <summary>
    /// The wind bed: one looping source whose level wanders slowly.
    ///
    /// A fixed loop played for a minute stops being weather and becomes a sound effect — the ear
    /// finds the period, and a 45 s loop is short enough to notice. A slow drift in level is
    /// enough to break that without a second recording: it is the difference between a fan and
    /// air moving.
    ///
    /// Perlin noise rather than a sine. A sine is a rhythm and can be predicted; noise at this
    /// rate cannot, and the drift reads as weather rather than as a tremolo.
    ///
    /// The source is 2D on purpose. Wind is all around you, so there is nothing to place: a 3D
    /// source would swing as the player turns, which is the one thing a bed must not do.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public class WindAudio : MonoBehaviour
    {
        [SerializeField] float baseVolume = 0.18f;

        // Fraction of baseVolume the level may swing either side. A fifth is a quiet room
        // changing, not a gust.
        [Range(0f, 0.9f)] [SerializeField] float drift = 0.20f;

        // Seconds for the drift to cross from one extreme to the other. The first pass used 37 s,
        // which breathed at almost the same rate as the 45 s loop: the two cycles reinforced each
        // other and made the repetition of the recording easier to hear. Slower than the loop by
        // a factor of two and a half, so the level is no longer a second rhythm pointing at the
        // first.
        [SerializeField] float period = 110f;

        AudioSource source;
        float seed;

        void Awake()
        {
            source = GetComponent<AudioSource>();
            seed = Random.value * 100f;

            // Phase 7 configures these too, but the component's behaviour depends on them, and
            // re-applying them means the bed cannot come up positional or silent because of a
            // stale inspector value.
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = baseVolume;
        }

        void Start()
        {
            // A safety net rather than the normal path: Phase 7 also sets playOnAwake. If the
            // source is ever reused with that cleared, the bed should still come up.
            if (source != null && source.clip != null && !source.isPlaying)
                source.Play();
        }

        void Update()
        {
            if (source == null)
                return;

            float n = Mathf.PerlinNoise(seed, Time.time / period) * 2f - 1f;
            source.volume = baseVolume * (1f + drift * n);
        }
    }
}
