using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace StardewModdingAPI.Events
{
    /// <summary>Event arguments for a <see cref="IWorldEvents.DebrisListChanged"/> event.</summary>
    public class WorldDebrisListChangedEventArgs : EventArgs
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The location which changed.</summary>
        public GameLocation Location { get; }

        /// <summary>The debris added to the location.</summary>
        public IEnumerable<Debris> Added { get; }

        /// <summary>The debris removed from the location.</summary>
        public IEnumerable<Debris> Removed { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="location">The location which changed.</param>
        /// <param name="added">The debris added to the location.</param>
        /// <param name="removed">The debris removed from the location.</param>
        public WorldDebrisListChangedEventArgs(GameLocation location, IEnumerable<Debris> added, IEnumerable<Debris> removed)
        {
            this.Location = location;
            this.Added = added.ToArray();
            this.Removed = removed.ToArray();
        }
    }
}
