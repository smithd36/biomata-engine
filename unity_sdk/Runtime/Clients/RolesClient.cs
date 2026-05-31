// Biomata.SDK — RolesClient.cs
// Fetches role definitions from the running backend.

using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Retrieves the role manifest declared in the backend sim.yaml.
    /// Call <see cref="ListAsync"/> once after connect to seed
    /// <see cref="RoleManifestLoader"/> so agents can resolve role defaults
    /// without a static BiomataRoles.json file.
    /// </summary>
    public class RolesClient
    {
        private readonly ITransport _transport;

        internal RolesClient(ITransport transport) => _transport = transport;

        /// <summary>
        /// Fetch all roles declared in the backend sim.yaml.
        /// Returns the same shape as BiomataRoles.json would have contained.
        /// </summary>
        public Task<RolesData> ListAsync(CancellationToken ct = default)
            => _transport.RolesListAsync(ct);
    }
}
