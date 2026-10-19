using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeadPines.Interaction
{
    /// <summary>
    /// Looks down the camera and searches whatever is in front of it.
    ///
    /// The loop is deliberately one button: look at a container, a prompt appears, hold the
    /// interact button, a bar fills, an item is taken. One hold yields one item, so a full
    /// cupboard takes several holds and the room makes you stand still in it — the search is a
    /// commitment, not a keypress. Releasing after a successful search is required before the
    /// next one starts (`needsRelease`), so holding the button down does not vacuum a container
    /// dry; the player has to let go and press again, which also gives the toast time to read.
    ///
    /// The aim is a sphere cast rather than a thin ray: a container is a solid box with no
    /// crosshair-sized feature to aim at, and hunting for the exact pixel that brings up the
    /// prompt is not the tension the room is supposed to have. The sphere is small (default
    /// 15 cm) and still stops at the first collider, so walls and floors block as before.
    /// The Interactable is looked for on the hit collider's parents, so a child mesh can carry
    /// the visible surface while the component sits on the prop root.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerInteractor : MonoBehaviour
    {
        [Tooltip("The first-person camera. The sphere is cast from its position along its forward.")]
        public Camera view;

        [Tooltip("The project's InputActionAsset. The Player map's Interact action is used.")]
        public InputActionAsset actions;

        [Tooltip("Receives found items.")]
        public Inventory inventory;

        [Tooltip("How far in front of the camera a container can be searched.")]
        public float reach = 2.5f;

        [Tooltip("Radius of the aim sphere. A little forgiveness around the view centre.")]
        public float aimRadius = 0.15f;

        InputAction interactAction;

        Interactable target;
        float progress;
        bool needsRelease;

        public Interactable Target => target;

        /// <summary>0..1 while the hold is in progress; reset to 0 on look-away or release.</summary>
        public float Progress => progress;

        /// <summary>Fires once per item taken, after it is in the inventory.</summary>
        public event Action<ItemDefinition> Found;

        /// <summary>
        /// The key or button shown in the prompt. Read from the action's first binding rather
        /// than hard-coded, so remapping the action keeps the prompt honest. The gamepad binding
        /// is not selected; on a pad the prompt still says the keyboard key, which is a known
        /// M1 simplification.
        /// </summary>
        public string HoldLabel
        {
            get
            {
                if (interactAction == null)
                    return "E";

                string label = interactAction.GetBindingDisplayString(0);
                return string.IsNullOrEmpty(label) ? "E" : label;
            }
        }

        void OnEnable()
        {
            Bind();

            if (interactAction != null)
                interactAction.Enable();
        }

        void OnDisable()
        {
            if (interactAction != null)
                interactAction.Disable();

            target = null;
            progress = 0f;
            needsRelease = false;
        }

        void Bind()
        {
            if (actions == null)
            {
                Debug.LogError("[Dead Pines] PlayerInteractor has no InputActionAsset — "
                               + "container searching is off. Re-run the M1 containers menu item.",
                               this);
                return;
            }

            var map = actions.FindActionMap("Player", false);

            if (map == null)
            {
                Debug.LogError("[Dead Pines] PlayerInteractor: no 'Player' action map in "
                               + actions.name + ".", this);
                return;
            }

            interactAction = map.FindAction("Interact", false);

            if (interactAction == null)
            {
                Debug.LogError("[Dead Pines] PlayerInteractor: the 'Player' map has no "
                               + "'Interact' action.", this);
                return;
            }
        }

        void Update()
        {
            Interactable found = FindTarget();

            if (found != target)
            {
                target = found;
                progress = 0f;
            }

            bool held = interactAction != null && interactAction.IsPressed();

            if (!held)
                needsRelease = false;

            if (target == null || target.Searched || !held || needsRelease)
            {
                progress = 0f;
                return;
            }

            float duration = Mathf.Max(0.05f, target.holdSeconds);
            progress += Time.deltaTime / duration;

            if (progress < 1f)
                return;

            progress = 0f;
            needsRelease = true;

            ItemDefinition item = target.Search();

            if (item == null)
                return;

            if (inventory != null)
                inventory.Add(item, 1);

            if (Found != null)
                Found(item);
        }

        Interactable FindTarget()
        {
            if (view == null)
                return null;

            var ray = new Ray(view.transform.position, view.transform.forward);
            float radius = Mathf.Max(0.02f, aimRadius);

            RaycastHit hit;

            if (!Physics.SphereCast(ray, radius, out hit, reach, ~0,
                                    QueryTriggerInteraction.Ignore))
                return null;

            return hit.collider.GetComponentInParent<Interactable>();
        }
    }
}
