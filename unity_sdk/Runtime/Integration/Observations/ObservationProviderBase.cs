using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Base class for all observation data sources. Attach multiple concrete providers to
    /// the same GameObject as <see cref="ObservationCollector"/> to compose the observation
    /// dictionary that is sent to the backend each tick.
    /// </summary>
    public abstract class ObservationProviderBase : MonoBehaviour
    {
        /// <summary>
        /// Populate <paramref name="observation"/> with this provider's data.
        /// Keys written here appear directly in the agent's observation dict on the Python side.
        /// Called once per simulation tick before the RPC is issued.
        /// </summary>
        public abstract void Populate(Dictionary<string, object> observation);
    }
}
