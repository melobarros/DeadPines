using UnityEngine;
using UnityEngine.InputSystem;

namespace DeadPines.Player
{
    /// <summary>
    /// M0 first-person movement: walk, run, crouch.
    ///
    /// ROADMAP.md asks for exactly those three and nothing else — no jump, no head bob, no
    /// stamina — and adds "M0 and M1 need no character at all", so this drives a camera, not a
    /// rigged body. The M0 success test is walking around for five minutes, which needs the
    /// movement to feel right and needs nothing else.
    ///
    /// Structure is a body transform carrying the CharacterController with the camera on a head
    /// child. Yaw goes on the body, pitch on the head, which is what keeps the view from rolling
    /// as you turn; it also makes crouch clean, because the eye can drop without moving the
    /// capsule that decides where the feet are.
    ///
    /// Bindings come from Assets/InputSystem_Actions.inputactions, the asset the Unity template
    /// already ships. It already binds all four actions this needs — Move on WASD, Look on mouse
    /// delta, Crouch on C, Sprint on left Shift — plus gamepad equivalents, and an asset is
    /// rebindable later without touching this file. The project runs the new Input System only
    /// (activeInputHandler: 1), so UnityEngine.Input would throw rather than quietly fall back.
    ///
    /// Actions are looked up by name rather than through a generated wrapper class. Codegen would
    /// have to complete before this file could compile, which is an ordering problem to create
    /// inside an editor script for no benefit at this size.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] InputActionAsset actions;
        [SerializeField] Transform head;

        [Header("Speed (m/s)")]
        [SerializeField] float walkSpeed = 2.6f;
        [SerializeField] float runSpeed = 5.2f;
        [SerializeField] float crouchSpeed = 1.4f;

        [Header("Eye height (m)")]
        [SerializeField] float standEyeHeight = 1.70f;
        [SerializeField] float crouchEyeHeight = 1.05f;

        [Header("Capsule (m)")]
        [SerializeField] float standHeight = 1.80f;
        [SerializeField] float crouchHeight = 1.20f;
        // Metres per second of travel for the capsule and the eye during a crouch or stand.
        // Snapping reads as a glitch; this is about a fifth of a second.
        [SerializeField] float crouchTransition = 3f;

        [Header("Look")]
        // Degrees of rotation per pixel of mouse delta. Roughly 25 cm of mouse travel per full
        // turn at 800 DPI. Personal, so it is a serialized field — tune it in the inspector
        // rather than in code.
        [SerializeField] float degreesPerPixel = 0.05f;
        [SerializeField] float pitchLimit = 89f;

        [Header("Motion")]
        // Heavier than 9.81. Game gravity is a feel decision, not a physics one, and real
        // gravity makes a jump — or in M0, a step off a rise — feel floaty.
        [SerializeField] float gravity = -20f;
        // A small downward push while grounded. Without it isGrounded flickers on slopes and
        // the controller micro-bounces down them.
        [SerializeField] float groundStick = -2f;

        CharacterController controller;
        InputAction moveAction;
        InputAction lookAction;
        InputAction crouchAction;
        InputAction sprintAction;

        float pitch;
        float verticalVelocity;
        float eyeHeight;
        bool crouching;
        float distanceTravelled;

        /// <summary>
        /// Horizontal metres walked since the scene loaded. M0 wants "footsteps synced to actual
        /// steps", and stride length is a movement fact rather than an audio one, so the audio
        /// pass reads steps off this rather than re-deriving distance itself.
        /// </summary>
        public float DistanceTravelled => distanceTravelled;

        public bool IsCrouching => crouching;

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            eyeHeight = standEyeHeight;

            if (head == null)
                Debug.LogError("[Dead Pines] FirstPersonController has no head assigned — the " +
                               "camera will not pitch and crouch will not move it.", this);
        }

        void OnEnable()
        {
            if (!Bind())
                return;

            // InputActionMap is a plain class, not a UnityEngine.Object, so this needs no
            // fake-null care — but the asset that owns it does, which is why actions is checked
            // with == rather than ??.
            var map = actions.FindActionMap("Player", false);
            if (map != null)
                map.Enable();

            SetCursorLocked(true);
        }

        void OnDisable()
        {
            SetCursorLocked(false);

            if (actions == null)
                return;

            var map = actions.FindActionMap("Player", false);
            if (map != null)
                map.Disable();
        }

        // Alt-tabbing out has to release the cursor or it stays trapped in the window with the
        // look still live, and coming back has to re-lock it or the mouse escapes on the next
        // turn. Unity does not do either on its own.
        void OnApplicationFocus(bool hasFocus)
        {
            SetCursorLocked(hasFocus);
        }

        /// <summary>
        /// The cursor lock is what makes a first-person camera testable at all: without it the
        /// mouse leaves the Game view mid-turn and looking simply stops responding.
        ///
        /// There is deliberately no unlock key. In the Editor, Escape releases the lock on its
        /// own, which is the escape hatch while testing; in a build, unlocking belongs to a pause
        /// menu, and M0 has no UI at all — "not even a crosshair". That is M1's problem.
        /// </summary>
        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        /// <summary>
        /// Resolves the four actions, or reports exactly which one is missing. Returns false so
        /// the caller stops rather than running Update against half-bound input.
        /// </summary>
        bool Bind()
        {
            if (actions == null)
            {
                Debug.LogError("[Dead Pines] FirstPersonController has no InputActionAsset " +
                               "assigned — movement is off. Run Dead Pines/Phase 3 to wire it.", this);
                return false;
            }

            var map = actions.FindActionMap("Player", false);
            if (map == null)
            {
                Debug.LogError($"[Dead Pines] {actions.name} has no 'Player' action map.", this);
                return false;
            }

            moveAction = map.FindAction("Move", false);
            lookAction = map.FindAction("Look", false);
            crouchAction = map.FindAction("Crouch", false);
            sprintAction = map.FindAction("Sprint", false);

            // Missing bindings fail loudly. The alternative is a player who cannot move, with
            // nothing in the Console to say why.
            bool complete = true;
            complete &= Report(moveAction, "Move", map);
            complete &= Report(lookAction, "Look", map);
            complete &= Report(crouchAction, "Crouch", map);
            complete &= Report(sprintAction, "Sprint", map);

            return complete;
        }

        bool Report(InputAction action, string name, InputActionMap map)
        {
            if (action != null)
                return true;

            Debug.LogError($"[Dead Pines] The 'Player' map has no '{name}' action. " +
                           $"Run, sprint and crouch need Move, Look, Crouch and Sprint.", this);
            return false;
        }

        void Update()
        {
            if (moveAction == null)
                return;

            Look();
            Crouch();
            Move();
        }

        void Look()
        {
            if (lookAction == null)
                return;

            Vector2 delta = lookAction.ReadValue<Vector2>() * degreesPerPixel;

            // Pitch is clamped and lives on the head; yaw is unbounded and lives on the body.
            // Putting both on one transform is what makes a first-person camera roll.
            pitch = Mathf.Clamp(pitch - delta.y, -pitchLimit, pitchLimit);

            if (head != null)
                head.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            // The body only ever yaws, so its forward stays horizontal and no roll accumulates.
            transform.Rotate(0f, delta.x, 0f, Space.Self);
        }

        void Crouch()
        {
            // Crouch is a Button bound to C — a momentary key — so a press toggles. Holding a
            // momentary key to stay crouched is the worse fit for that binding.
            if (crouchAction != null && crouchAction.WasPressedThisFrame())
                crouching = !crouching;

            float targetHeight = crouching ? crouchHeight : standHeight;
            float targetEye = crouching ? crouchEyeHeight : standEyeHeight;

            // Capsule and eye move at the same rate, so the camera never spends a frame outside
            // the collider that is supposed to contain it.
            controller.height = Mathf.MoveTowards(controller.height, targetHeight,
                                                  crouchTransition * Time.deltaTime);
            controller.center = new Vector3(0f, controller.height * 0.5f, 0f);

            eyeHeight = Mathf.MoveTowards(eyeHeight, targetEye, crouchTransition * Time.deltaTime);

            if (head != null)
                head.localPosition = new Vector3(0f, eyeHeight, 0f);

            // Standing up under low geometry is unchecked because M0 has no low geometry — the
            // world is terrain. M1 adds a building you can enter, and that is when a capsule
            // overlap test before standing belongs here.
        }

        void Move()
        {
            Vector2 input = moveAction.ReadValue<Vector2>();

            // The body's forward and right are always horizontal, since only the head pitches.
            Vector3 wish = transform.right * input.x + transform.forward * input.y;

            // Diagonals must not be faster than forward. The 2DVector composite already clamps a
            // keyboard to a circle, but a partly deflected stick should stay slow, so this
            // clamps the magnitude rather than always normalising.
            if (wish.sqrMagnitude > 1f)
                wish.Normalize();

            bool running = sprintAction != null && sprintAction.IsPressed() && !crouching;
            float speed = crouching ? crouchSpeed : running ? runSpeed : walkSpeed;

            if (controller.isGrounded && verticalVelocity < 0f)
                verticalVelocity = groundStick;
            else
                verticalVelocity += gravity * Time.deltaTime;

            Vector3 motion = wish * speed + Vector3.up * verticalVelocity;

            Vector3 before = transform.position;
            controller.Move(motion * Time.deltaTime);

            // Actual displacement, flattened. Using controller.velocity here would count falling
            // as walking, and footsteps should not fire on the way down a slope.
            Vector3 moved = transform.position - before;
            moved.y = 0f;
            distanceTravelled += moved.magnitude;
        }
    }
}
