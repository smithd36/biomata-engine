using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Adds world-space position, optional yaw rotation, and optional velocity to the
    /// agent's observation dictionary. Reads <see cref="Rigidbody"/> for velocity when
    /// one is present on the same or a parent GameObject.
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Transform")]
    public class TransformObservationProvider : ObservationProviderBase
    {
        [SerializeField] private bool includeRotation = true;
        [SerializeField] private bool includeVelocity = true;

        private Rigidbody _rb;

        private void Awake() => _rb = GetComponentInParent<Rigidbody>();

        public override void Populate(Dictionary<string, object> observation)
        {
            var p = transform.position;
            observation["position_x"] = (double)p.x;
            observation["position_y"] = (double)p.y;
            observation["position_z"] = (double)p.z;

            if (includeRotation)
                observation["rotation_y"] = (double)transform.eulerAngles.y;

            if (includeVelocity && _rb != null)
            {
                observation["velocity_x"] = (double)_rb.linearVelocity.x;
                observation["velocity_z"] = (double)_rb.linearVelocity.z;
            }
        }
    }
}
