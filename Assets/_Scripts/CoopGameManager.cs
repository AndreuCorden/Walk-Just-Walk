using UnityEngine;
using Unity.Netcode;

public class CoopGameManager : NetworkBehaviour
{
    [SerializeField] private GameObject sharedBodyPrefab;
    [SerializeField] private Transform spawnPoint;

    private int connectedPlayers = 0;
    private GameObject spawnedBody;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
    }

    private void OnClientConnected(ulong clientId)
    {
        connectedPlayers++;

        // When two players are connected, spawn the single shared body
        if (connectedPlayers == 2 && spawnedBody == null)
        {
            spawnedBody = Instantiate(sharedBodyPrefab, spawnPoint.position, spawnPoint.rotation);
            
            // Spawn with Server ownership (No client owns this object directly)
            spawnedBody.GetComponent<NetworkObject>().Spawn();

            // Assign roles to clients via a ClientRpc
            AssignRolesClientRpc();
        }
    }

    [ClientRpc]
    private void AssignRolesClientRpc()
    {
        // Find the spawned body in the client scene
        SharedBodyController controller = FindFirstObjectByType<SharedBodyController>();
        if (controller != null)
        {
            controller.InitializeLocalPlayerRole();
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
    }
}