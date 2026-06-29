using UnityEngine;

public class SceneCameraManager : MonoBehaviour
{
    public static SceneCameraManager Instance;

    [Header("Direct Camera Assignments")]
    [SerializeField] private Camera player1Camera;
    [SerializeField] private Camera player2Camera;

    [Header("Audio Listeners")]
    [SerializeField] private AudioListener player1Audio;
    [SerializeField] private AudioListener player2Audio;

    private void Awake()
    {
        // Set up a clean local singleton pattern
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public void SetupLocalPlayerCamera(bool isPlayer1)
    {
        if (isPlayer1)
        {
            // Turn on Player 1 View, Turn off Player 2 View
            if (player1Camera != null) player1Camera.gameObject.SetActive(true);
            if (player2Camera != null) player2Camera.gameObject.SetActive(false);
            
            if (player1Audio != null) player1Audio.enabled = true;
            if (player2Audio != null) player2Audio.enabled = false;
            
            Debug.Log("<color=cyan>[CameraManager] Confirmed: This machine is Player 1 (Waist).</color>");
        }
        else
        {
            // Turn on Player 2 View, Turn off Player 1 View
            if (player1Camera != null) player1Camera.gameObject.SetActive(false);
            if (player2Camera != null) player2Camera.gameObject.SetActive(true);
            
            if (player1Audio != null) player1Audio.enabled = false;
            if (player2Audio != null) player2Audio.enabled = true;
            
            Debug.Log("<color=green>[CameraManager] Confirmed: This machine is Player 2 (Legs).</color>");
        }
    }
}