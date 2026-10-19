using UnityEngine;

namespace DeadPines.Audio
{
    /// <summary>
    /// Occasional crow calls placed around the listener.
    ///
    /// D25 asks for "wind under birds, occasional close birds", and the distinction that matters
    /// is distance. A call at a fixed level somewhere in front is a sample; a call that arrives
    /// from a direction, at a level that carries how far away it is, is a bird in the trees. So
    /// every call is mono — a stereo clip cannot be positioned and Unity folds it flat to 2D —
    /// and every call is placed somewhere new.
    ///
    /// Singles and pairs are in the same pool. A pair is two caws with the real pause between
    /// them, which reads as a bird; the same single repeated every time reads as a sound effect.
    ///
    /// A small pool of sources is reused rather than creating and destroying a GameObject per
    /// call. Calls are 9 seconds apart at the closest, so three voices never contend, and nothing
    /// is allocated after Awake.
    /// </summary>
    [DisallowMultipleComponent]
    public class BirdAudio : MonoBehaviour
    {
        [Header("Clips")]
        [SerializeField] AudioClip[] calls;

        [Header("Timing (s)")]
        [SerializeField] float minInterval = 9f;
        [SerializeField] float maxInterval = 30f;

        [Header("Placement (m)")]
        // Two bands rather than one uniform draw. A uniform 10-110 m puts most calls far away,
        // which is right, but then the close call D25 asks for only happens by accident. A quarter
        // of the calls are drawn from the near band instead.
        [SerializeField] float nearMin = 10f;
        [SerializeField] float nearMax = 30f;
        [SerializeField] float farMin = 30f;
        [SerializeField] float farMax = 110f;
        [Range(0f, 1f)] [SerializeField] float closeChance = 0.25f;

        [SerializeField] float minHeight = 6f;
        [SerializeField] float maxHeight = 18f;

        [Header("Mix")]
        [SerializeField] float volume = 0.9f;
        [Range(0f, 0.5f)] [SerializeField] float volumeJitter = 0.2f;
        [Range(0f, 0.1f)] [SerializeField] float pitchJitter = 0.03f;

        [Header("Distance")]
        // Where the call stops getting louder as it approaches, and where it goes silent. The
        // near figure is below the closest spawn distance, so a close call is never at full
        // volume: it is closer than the rest, not in your ear.
        [SerializeField] float rolloffNear = 8f;
        [SerializeField] float rolloffFar = 180f;

        [SerializeField] int voices = 3;

        AudioSource[] pool;
        Transform ear;
        int next;
        float timer;

        void Awake()
        {
            pool = new AudioSource[Mathf.Max(1, voices)];

            for (int i = 0; i < pool.Length; i++)
            {
                var go = new GameObject("BirdVoice" + (i + 1));
                go.transform.SetParent(transform, false);

                var source = go.AddComponent<AudioSource>();

                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = rolloffNear;
                source.maxDistance = rolloffFar;

                // A crow 80 m away moving 2 m between calls must not swoop. Doppler on a
                // stationary call is an artefact, not an effect.
                source.dopplerLevel = 0f;
                source.spread = 0f;

                pool[i] = source;
            }
        }

        void Start()
        {
            var listener = FindFirstObjectByType<AudioListener>();
            ear = listener != null ? listener.transform : null;

            timer = Random.Range(minInterval, maxInterval);
        }

        void Update()
        {
            if (calls == null || calls.Length == 0)
                return;

            timer -= Time.deltaTime;
            if (timer > 0f)
                return;

            Play();
            timer = Random.Range(minInterval, maxInterval);
        }

        void Play()
        {
            if (ear == null)
                return;

            AudioClip clip = calls[Random.Range(0, calls.Length)];
            if (clip == null)
                return;

            float distance = Random.value < closeChance
                ? Random.Range(nearMin, nearMax)
                : Random.Range(farMin, farMax);

            // A random bearing around the listener, lifted into the canopy. Added to the
            // listener's height rather than read from the terrain: the player is always on the
            // ground, so this keeps a call above the trees whatever the slope.
            Vector3 direction = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;

            AudioSource source = pool[next];
            next = (next + 1) % pool.Length;

            source.transform.position = ear.position + direction * distance
                                        + Vector3.up * Random.Range(minHeight, maxHeight);

            source.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            source.volume = volume * Random.Range(1f - volumeJitter, 1f);
            source.clip = clip;
            source.Play();
        }
    }
}
