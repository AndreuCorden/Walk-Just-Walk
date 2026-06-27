using Unity.Netcode;
using UnityEngine;

public class NetworkBootstrapUI : MonoBehaviour
{
    void OnGUI()
    {
        // SAFETY CHECK: If the network is shutting down or missing, do nothing.
        if (NetworkManager.Singleton == null) return;

        GUILayout.BeginArea(new Rect(10, 10, 200, 200));
        
        if (!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            if (GUILayout.Button("Start Host (Player 1)"))
            {
                NetworkManager.Singleton.StartHost();
            }
            if (GUILayout.Button("Start Client (Player 2)"))
            {
                NetworkManager.Singleton.StartClient();
            }
        }
        else
        {
            GUILayout.Label($"Running as: {(NetworkManager.Singleton.IsHost ? "Host / Player 1" : "Client / Player 2")}");
        }

        GUILayout.EndArea();
    }
}