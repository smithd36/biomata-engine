namespace Biomata.Integration
{
    /// <summary>
    /// Controls whether this agent's backend state is pre-existing or created by Unity at runtime.
    /// </summary>
    public enum AgentOwnershipMode
    {
        /// <summary>
        /// Agent already exists on the backend (declared in sim.yaml or registered by another client).
        /// Unity binds the visual shell to it — no registration RPC is sent.
        /// On destroy, the backend agent is left intact.
        /// </summary>
        BindToExisting,

        /// <summary>
        /// Agent is owned by this Unity client.
        /// Registered with the backend automatically on connect; unregistered on destroy.
        /// Requires a valid Brain Class.
        /// </summary>
        CreateAtRuntime,
    }
}
