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
            observation[ObservationKeys.PositionX] = (double)p.x;
            observation[ObservationKeys.PositionY] = (double)p.y;
            observation[ObservationKeys.PositionZ] = (double)p.z;

            if (includeRotation)
                observation[ObservationKeys.RotationY] = (double)transform.eulerAngles.y;

            if (includeVelocity && _rb != null)
            {
                observation[ObservationKeys.VelocityX] = (double)_rb.linearVelocity.x;
                observation[ObservationKeys.VelocityZ] = (double)_rb.linearVelocity.z;
            }
        }

        public override IReadOnlyCollection<string> DeclaredObservationKeys => new[]
        {
            ObservationKeys.PositionX, ObservationKeys.PositionY, ObservationKeys.PositionZ,
            ObservationKeys.RotationY, ObservationKeys.VelocityX, ObservationKeys.VelocityZ,
        };
    }
}
