using System.Collections.Generic;
using UnityEngine;

namespace DeadPines.Interaction
{
    /// <summary>
    /// Something the player can search by holding the interact button.
    ///
    /// The contents list is the loot table for this object, already resolved: the editor tooling
    /// fills it with item definitions, and Search() hands them out one at a time. The object is
    /// not destroyed when empty — an emptied crate is still a crate, and having it read "Empty"
    /// when you look at it again is the point. What is left in a container is exactly what was
    /// put there, so the room can be made tense by putting the right things in the right places
    /// rather than by rolling dice.
    /// </summary>
    public class Interactable : MonoBehaviour
    {
        [Tooltip("Lower-case noun used in the prompt: 'Hold E — Search the cupboard'.")]
        public string displayName = "container";

        [Tooltip("Seconds of held input needed to take one item.")]
        public float holdSeconds = 1.1f;

        [Tooltip("Taken from the front of this list, one per successful search.")]
        public List<ItemDefinition> contents = new List<ItemDefinition>();

        public bool Searched => contents.Count == 0;

        /// <summary>
        /// Takes and returns the next item, or null when there is nothing left. Callers get the
        /// item before it is removed, so the search and the removal cannot disagree.
        /// </summary>
        public ItemDefinition Search()
        {
            if (contents.Count == 0)
                return null;

            ItemDefinition item = contents[0];
            contents.RemoveAt(0);
            return item;
        }
    }
}
