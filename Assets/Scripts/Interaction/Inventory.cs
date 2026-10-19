using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeadPines.Interaction
{
    /// <summary>
    /// What the player is carrying.
    ///
    /// A list of item-and-count pairs rather than one entry per found object: the HUD needs one
    /// cell per kind of thing with a count on it, and a survival inventory will always want
    /// stacking. Entries keep insertion order, so the strip does not shuffle when a count grows.
    ///
    /// The Changed event is how the HUD redraws. It fires on every add, including adds that only
    /// raise a count, so the HUD never has to poll the component.
    /// </summary>
    public class Inventory : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            public ItemDefinition item;
            public int count;
        }

        [SerializeField] List<Entry> entries = new List<Entry>();

        public event Action Changed;

        public IReadOnlyList<Entry> Entries => entries;

        public int Total
        {
            get
            {
                int total = 0;

                for (int i = 0; i < entries.Count; i++)
                    total += entries[i].count;

                return total;
            }
        }

        public int CountOf(ItemDefinition item)
        {
            if (item == null)
                return 0;

            for (int i = 0; i < entries.Count; i++)
                if (entries[i].item == item)
                    return entries[i].count;

            return 0;
        }

        /// <summary>
        /// Adds a found item. Silently ignores a null definition so a mis-wired container can
        /// never crash the hold loop, and ignores non-positive counts.
        /// </summary>
        public void Add(ItemDefinition item, int count = 1)
        {
            if (item == null || count <= 0)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].item != item)
                    continue;

                entries[i].count += count;

                if (Changed != null)
                    Changed();

                return;
            }

            entries.Add(new Entry { item = item, count = count });

            if (Changed != null)
                Changed();
        }
    }
}
