using UnityEngine;

namespace AnimarsCatcher.Mono.Items
{
    public class BlueprintRotation : MonoBehaviour
    {
        public Transform player;
        public float rotationSpeed = 90f;
        public float detectionDistance = 5f;

        void Update()
        {

            float distance = Vector3.Distance(transform.position, player.position);

            if (distance > detectionDistance)
            {
                transform.Rotate(Vector3.forward, rotationSpeed * Time.deltaTime);
            }
        }

    }
}