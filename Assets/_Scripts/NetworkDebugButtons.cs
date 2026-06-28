using UnityEngine;
using Unity.Netcode;

public class NetworkDebugButtons : MonoBehaviour
{
    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 300));
        if (!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            if (GUILayout.Button("Start Host (Player 1)")) NetworkManager.Singleton.StartHost();
            if (GUILayout.Button("Start Client (Player 2)")) NetworkManager.Singleton.StartClient();
        }
        GUILayout.EndArea();
    }
}