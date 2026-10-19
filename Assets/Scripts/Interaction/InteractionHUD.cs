using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DeadPines.Interaction
{
    /// <summary>
    /// The interaction and inventory layer, built at runtime.
    ///
    /// There is no Canvas in M0 and no TextMeshPro assets in the project, so the HUD is
    /// assembled in code from uGUI and the built-in LegacyRuntime font. Building it here rather
    /// than saving a prefab keeps the scene free of UI plumbing and means the layout follows the
    /// scaler at any resolution without a hand-authored RectTransform to drift.
    ///
    /// Everything is anchored to the bottom edge: the prompt and its progress bar sit centre,
    /// the found toast just above them, and the inventory strip in the bottom-left corner. No
    /// panel is interactive, so no EventSystem is needed.
    /// </summary>
    public class InteractionHUD : MonoBehaviour
    {
        [Tooltip("The player's interactor. Found automatically when left empty.")]
        public PlayerInteractor interactor;

        [Tooltip("The player's inventory. Found automatically when left empty.")]
        public Inventory inventory;

        static readonly Color Bone = new Color(0.84f, 0.80f, 0.70f, 1f);
        static readonly Color ToastGold = new Color(0.93f, 0.86f, 0.68f, 1f);

        const float ToastSeconds = 2.4f;
        const float ToastFadeSeconds = 0.6f;

        Font font;

        RectTransform promptPanel;
        Text promptText;
        GameObject barBack;
        RectTransform barFill;

        Text toastText;
        Shadow toastShadow;
        float toastUntil;

        RectTransform strip;
        readonly List<GameObject> cells = new List<GameObject>();

        void Awake()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (font == null)
                Debug.LogError("[Dead Pines] InteractionHUD could not load the LegacyRuntime "
                               + "font — the HUD will render no text.", this);

            Build();
        }

        void Start()
        {
            if (interactor == null)
                interactor = FindFirstObjectByType<PlayerInteractor>();

            if (inventory == null)
                inventory = FindFirstObjectByType<Inventory>();

            if (interactor != null)
                interactor.Found += OnFound;

            if (inventory != null)
                inventory.Changed += RebuildInventory;

            RebuildInventory();

            promptPanel.gameObject.SetActive(false);
            toastText.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (interactor != null)
                interactor.Found -= OnFound;

            if (inventory != null)
                inventory.Changed -= RebuildInventory;
        }

        void Update()
        {
            Interactable target = interactor != null ? interactor.Target : null;
            bool hasTarget = target != null;

            if (promptPanel.gameObject.activeSelf != hasTarget)
                promptPanel.gameObject.SetActive(hasTarget);

            if (hasTarget)
            {
                if (target.Searched)
                {
                    promptText.text = "Empty";
                    barBack.SetActive(false);
                }
                else
                {
                    promptText.text = "Hold " + interactor.HoldLabel
                                      + " — Search the " + target.displayName;
                    barBack.SetActive(true);
                    barFill.localScale = new Vector3(Mathf.Clamp01(interactor.Progress), 1f, 1f);
                }
            }

            if (toastText.gameObject.activeSelf)
            {
                float left = toastUntil - Time.time;

                if (left <= 0f)
                {
                    toastText.gameObject.SetActive(false);
                }
                else
                {
                    float alpha = Mathf.Clamp01(left / ToastFadeSeconds);
                    Fade(toastText, alpha);
                    Fade(toastShadow, alpha);
                }
            }
        }

        void OnFound(ItemDefinition item)
        {
            if (item == null)
                return;

            toastText.text = "Found — " + item.displayName;

            Fade(toastText, 1f);
            Fade(toastShadow, 1f);

            toastText.gameObject.SetActive(true);
            toastUntil = Time.time + ToastSeconds;
        }

        static void Fade(Graphic graphic, float alpha)
        {
            if (graphic == null)
                return;

            Color color = graphic.color;
            color.a = alpha;
            graphic.color = color;
        }

        static void Fade(Shadow shadow, float alpha)
        {
            if (shadow == null)
                return;

            Color color = shadow.effectColor;
            color.a = alpha;
            shadow.effectColor = color;
        }

        // ---------------------------------------------------------------- inventory strip

        void RebuildInventory()
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i] == null)
                    continue;

                cells[i].SetActive(false);
                Destroy(cells[i]);
            }

            cells.Clear();

            if (inventory == null)
                return;

            var entries = inventory.Entries;

            if (entries.Count == 0)
            {
                strip.gameObject.SetActive(false);
                return;
            }

            strip.gameObject.SetActive(true);
            strip.sizeDelta = new Vector2(10f + entries.Count * 54f, 64f);

            for (int i = 0; i < entries.Count; i++)
            {
                Inventory.Entry entry = entries[i];

                var cell = Panel(strip, "Item_" + i, new Vector2(0f, 0f), new Vector2(0f, 0f),
                                 new Vector2(8f + i * 54f, 8f), new Vector2(48f, 48f));

                var backdrop = cell.gameObject.AddComponent<Image>();
                backdrop.color = new Color(0f, 0f, 0f, 0.45f);
                backdrop.raycastTarget = false;

                bool hasIcon = entry.item != null && entry.item.icon != null;

                var iconRect = Panel(cell, "Icon", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                     Vector2.zero, new Vector2(40f, 40f));

                var icon = iconRect.gameObject.AddComponent<Image>();
                icon.sprite = hasIcon ? entry.item.icon : null;
                icon.color = hasIcon ? Color.white : new Color(1f, 1f, 1f, 0.10f);
                icon.preserveAspect = true;
                icon.raycastTarget = false;

                if (entry.count > 1)
                {
                    var count = Label(cell, "Count", 16, TextAnchor.LowerRight);
                    var countRect = count.rectTransform;
                    countRect.anchorMin = new Vector2(1f, 0f);
                    countRect.anchorMax = new Vector2(1f, 0f);
                    countRect.pivot = new Vector2(1f, 0f);
                    countRect.anchoredPosition = new Vector2(-2f, 0f);
                    countRect.sizeDelta = new Vector2(26f, 18f);
                    count.text = "x" + entry.count;
                    count.color = new Color(Bone.r, Bone.g, Bone.b, 0.95f);

                    var shadow = count.gameObject.AddComponent<Shadow>();
                    shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
                    shadow.effectDistance = new Vector2(1f, -1f);
                }

                cells.Add(cell.gameObject);
            }
        }

        // ---------------------------------------------------------------- construction

        void Build()
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
                                          typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // Prompt and its bar, bottom centre.
            promptPanel = Panel(canvasGo.transform, "Prompt", new Vector2(0.5f, 0f),
                                new Vector2(0.5f, 0f), new Vector2(0f, 84f),
                                new Vector2(760f, 46f));

            promptText = Label(promptPanel, "Label", 26, TextAnchor.MiddleCenter);
            Stretch(promptText.rectTransform);
            promptText.color = Bone;
            promptText.text = "Hold E — Search the container";

            var shadow = promptText.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);

            var bar = Panel(promptPanel, "Bar", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                            new Vector2(0f, 0f), new Vector2(280f, 4f));

            var barImage = bar.gameObject.AddComponent<Image>();
            barImage.color = new Color(0f, 0f, 0f, 0.45f);
            barImage.raycastTarget = false;

            barBack = bar.gameObject;

            barFill = Panel(bar, "Fill", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                            Vector2.zero, new Vector2(280f, 4f));

            var fillImage = barFill.gameObject.AddComponent<Image>();
            fillImage.color = new Color(Bone.r, Bone.g, Bone.b, 0.9f);
            fillImage.raycastTarget = false;

            // Found toast, just above the prompt.
            toastText = Label(canvasGo.transform, "Toast", 22, TextAnchor.MiddleCenter);
            var toastRect = toastText.rectTransform;
            toastRect.anchorMin = new Vector2(0.5f, 0f);
            toastRect.anchorMax = new Vector2(0.5f, 0f);
            toastRect.pivot = new Vector2(0.5f, 0f);
            toastRect.anchoredPosition = new Vector2(0f, 140f);
            toastRect.sizeDelta = new Vector2(760f, 34f);
            toastText.color = ToastGold;
            toastText.text = "Found — Canned food";

            toastShadow = toastText.gameObject.AddComponent<Shadow>();
            toastShadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            toastShadow.effectDistance = new Vector2(1f, -1f);

            // Inventory strip, bottom left.
            strip = Panel(canvasGo.transform, "Inventory", new Vector2(0f, 0f),
                          new Vector2(0f, 0f), new Vector2(32f, 32f), new Vector2(64f, 64f));

            var stripImage = strip.gameObject.AddComponent<Image>();
            stripImage.color = new Color(0f, 0f, 0f, 0.35f);
            stripImage.raycastTarget = false;
        }

        static RectTransform Panel(Transform parent, string name, Vector2 anchor,
                                   Vector2 pivot, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        Text Label(Transform parent, string name, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.alignment = alignment;
            label.color = Bone;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
