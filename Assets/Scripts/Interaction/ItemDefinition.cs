using UnityEngine;

namespace DeadPines.Interaction
{
    /// <summary>
    /// One kind of thing you can find.
    ///
    /// A ScriptableObject rather than an enum or a string id: the item needs an icon, and the
    /// icon is an asset reference. Keeping the definition as an asset also means a container in
    /// the scene stores a reference to the item rather than a name that has to be matched up at
    /// runtime, and a loot table later is just a list of these.
    ///
    /// Fields are public because they are written by the editor tooling that creates the
    /// catalogue, the same way the scene-building scripts assign materials and dimensions.
    /// </summary>
    [CreateAssetMenu(menuName = "Dead Pines/Item", fileName = "Item")]
    public class ItemDefinition : ScriptableObject
    {
        [Tooltip("Stable identifier, used in logs and later in save data.")]
        public string id;

        [Tooltip("Shown in the found toast and the inventory tooltip.")]
        public string displayName;

        public Sprite icon;
    }
}
