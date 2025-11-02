using System;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.Items
{
    public class Blueprint : MonoBehaviour
    {
        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Player"))
            {
                EventBus.Instance.Publish(new BlueprintCollectedEventData());
                Destroy(gameObject);
            }
        }
    }
}