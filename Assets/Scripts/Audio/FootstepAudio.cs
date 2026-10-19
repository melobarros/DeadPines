using DeadPines.Player;
using UnityEngine;

namespace DeadPines.Audio
{
    /// <summary>
    /// Plays a footstep every stride of real travel, using the surface underfoot.
    ///
    /// Driven by distance, not by a timer. FirstPersonController already counts horizontal metres
    /// into DistanceTravelled, which makes steps land where the feet do: they stop when you walk
    /// into a wall, they come faster when you run, and they do not fire while sliding down a
    /// slope, because the counter only ever sees ground actually covered.
    ///
    /// The surface comes from Phase 2's splatmap rather than from colliders or a mesh lookup. The
    /// terrain already carries four layer weights per texel — forest floor, leaf litter, mud,
    /// moss and rock — so the heaviest weight under the player is the answer. Reading it costs one
    /// small array, a few times a second rather than every frame.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FirstPersonController))]
    public class FootstepAudio : MonoBehaviour
    {
        [System.Serializable]
        public class Surface
        {
            public string label;

            // Two pools, because the recordings are two takes. The Nox_Sound files hold walk
            // footsteps and run footsteps back to back, and they are not interchangeable: a run
            // footfall landing during a slow walk is the kind of detail that reads as broken.
            public AudioClip[] walk;
            public AudioClip[] run;
        }

        [Header("Surfaces")]
        // Index matches the splatmap layer order Phase 2 wrote: 0 forest floor, 1 leaf litter,
        // 2 mud, 3 moss and rock. A layer with no clips falls through to the fallback arrays, so
        // a surface we have not recorded yet is still audible rather than silent.
        [SerializeField] Surface[] layers = new Surface[0];
        [SerializeField] AudioClip[] fallbackWalk;
        [SerializeField] AudioClip[] fallbackRun;

        [Header("Stride (m per step)")]
        // Distance covered per footfall, not per gait cycle.
        //
        // These are set from the speeds in FirstPersonController against the cadence the
        // recordings were made at, not from anatomy. 2.6 m/s over 1.30 m is two steps a second,
        // which is a walking cadence. 5.2 m/s over 1.70 m is three, which is the pacing of the
        // rock take's fast section — its steps land 0.33 s apart, and three a second is 0.33 s.
        //
        // The first pass had 0.80 and 1.60, which put both gaits at the same 3.25 steps a second.
        // Walking would have stepped at exactly the speed of running, on the run recording's
        // pacing, and the only difference left between the two would have been the clip.
        [SerializeField] float walkStride = 1.30f;
        [SerializeField] float runStride = 1.70f;
        [SerializeField] float crouchStride = 0.90f;

        [Header("Mix")]
        [SerializeField] float volume = 0.55f;

        // Crouching is a stealth posture, so it should sound like one. A proportion of the walk
        // level rather than an absolute, so tuning the overall volume keeps crouching quiet by the
        // same amount instead of needing a second number kept in step by hand.
        [Range(0f, 1f)] [SerializeField] float crouchVolume = 0.55f;

        [Range(0f, 0.3f)] [SerializeField] float pitchJitter = 0.05f;
        [Range(0f, 0.5f)] [SerializeField] float volumeJitter = 0.12f;

        [Header("References")]
        [SerializeField] Terrain terrain;

        FirstPersonController controller;
        AudioSource source;

        float lastDistance;
        float carried;

        void Awake()
        {
            controller = GetComponent<FirstPersonController>();

            source = GetComponent<AudioSource>();
            if (source == null)
                source = gameObject.AddComponent<AudioSource>();

            source.playOnAwake = false;

            // The listener is the ear, and it sits on this same object. A footstep played at the
            // ear is what makes it read as your own foot rather than as a sound somewhere nearby.
            source.spatialBlend = 0f;

            if (terrain == null)
                terrain = Terrain.activeTerrain;

            lastDistance = controller != null ? controller.DistanceTravelled : 0f;
        }

        void Update()
        {
            if (controller == null || source == null)
                return;

            float distance = controller.DistanceTravelled;
            float delta = distance - lastDistance;
            lastDistance = distance;

            // A respawn, a teleport or a long hitch is not a stride. Without this a jump in
            // position banks a burst of footsteps that all fire on the next frame.
            if (delta < 0f || delta > 2f)
            {
                carried = 0f;
                return;
            }

            carried += delta;

            float stride = Stride();
            if (stride <= 0.01f || carried < stride)
                return;

            // One step per frame at most; the guard above is what stops a single frame crossing
            // two strides and stacking them onto one sample.
            carried -= stride;

            Play();
        }

        float Stride()
        {
            if (controller.IsCrouching)
                return crouchStride;

            return controller.IsRunning ? runStride : walkStride;
        }

        void Play()
        {
            AudioClip[] clips = Clips();

            if (clips == null || clips.Length == 0)
                return;

            AudioClip clip = clips[Random.Range(0, clips.Length)];
            if (clip == null)
                return;

            // Small jitter, deliberately. Pitch is not a variety tool for a heavy boot, and past
            // about 10% it stops being a boot at all. Level moves a little more, which is the part
            // the ear actually reads as variation.
            float level = volume * (controller.IsCrouching ? crouchVolume : 1f);

            source.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            source.PlayOneShot(clip, level * Random.Range(1f - volumeJitter, 1f));
        }

        AudioClip[] Clips()
        {
            Surface surface = SurfaceAt(transform.position);

            if (surface != null)
            {
                AudioClip[] pool = controller.IsRunning ? surface.run : surface.walk;

                // A surface with no run take uses its walk take rather than going quiet, which is
                // the difference between "not recorded yet" and "audio is broken".
                if (pool != null && pool.Length > 0)
                    return pool;

                if (surface.walk != null && surface.walk.Length > 0)
                    return surface.walk;
            }

            AudioClip[] fallback = controller.IsRunning ? fallbackRun : fallbackWalk;

            if (fallback != null && fallback.Length > 0)
                return fallback;

            return fallbackWalk;
        }

        Surface SurfaceAt(Vector3 worldPosition)
        {
            int layer = DominantLayer(worldPosition);

            if (layer >= 0 && layer < layers.Length && layers[layer] != null)
                return layers[layer];

            // No terrain, no alphamap, or a layer index past what was wired: the first layer is a
            // better guess than silence — it is the one that covers most of the tile.
            if (layers.Length > 0 && layers[0] != null)
                return layers[0];

            return null;
        }

        /// <summary>
        /// Which splatmap layer has the most weight at a world position, or -1 if it cannot be
        /// known. One texel is sampled rather than a window: the alphamap is a few hundred texels
        /// across a kilometre, so a step already spans a couple of them, and averaging would blur
        /// exactly the boundary a player walking along it is trying to read.
        /// </summary>
        int DominantLayer(Vector3 worldPosition)
        {
            if (terrain == null)
                return -1;

            TerrainData data = terrain.terrainData;
            if (data == null || data.terrainLayers == null || data.terrainLayers.Length == 0)
                return -1;

            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;

            if (size.x <= 0f || size.z <= 0f)
                return -1;

            int resolution = data.alphamapResolution;

            int x = Mathf.Clamp(Mathf.FloorToInt((worldPosition.x - origin.x) / size.x * resolution),
                                0, resolution - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt((worldPosition.z - origin.z) / size.z * resolution),
                                0, resolution - 1);

            float[,,] maps = data.GetAlphamaps(x, z, 1, 1);

            int best = 0;
            float bestWeight = maps[0, 0, 0];

            for (int l = 1; l < maps.GetLength(2); l++)
            {
                if (maps[0, 0, l] > bestWeight)
                {
                    bestWeight = maps[0, 0, l];
                    best = l;
                }
            }

            return best;
        }
    }
}
