// Biomata.SDK — ActionsClient.cs
// Fetches the live action space from the running backend.

using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Retrieves the action manifest the backend actually has loaded (from its action
    /// registry), not a committed JSON sidecar that may have drifted. Call
    /// <see cref="ListAsync"/> once after connect to validate handler coverage against
    /// the authoritative source — see <c>ActionManifestLoader.Populate</c>.
    /// </summary>
    public class ActionsClient
    {
        private readonly ITransport _transport;

        internal ActionsClient(ITransport transport) => _transport = transport;

        /// <summary>
        /// Fetch the backend's live action space. Same shape as BiomataActions.json.
        /// </summary>
        public Task<ManifestData> ListAsync(CancellationToken ct = default)
            => _transport.ActionsListAsync(ct);
    }
}
