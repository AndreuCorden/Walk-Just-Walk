using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem; // Added for the new Input System API

/// <summary>
/// Handles the shared control of a single body between Player 1 (Host/Server) 
/// and Player 2 (Client) using Unity's new Input System.
/// </summary>
public class SharedBodyController : NetworkBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float rotationSpeed = 90f;
    [SerializeField] private float leanSpeed = 30f;
    [SerializeField] private float maxLeanAngle = 45f;

    [Header("Visual Placeholders")]
    [SerializeField] private Transform leftLegVisual;
    [SerializeField] private Transform rightLegVisual;

    // NetworkVariables sync data from Server down to all Clients automatically
    private NetworkVariable<float> netCurrentLean = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netLeftLegRaised = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netRightLegRaised = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private float currentLeanAngle = 0f;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        Debug.Log($"Body Spawned. Role: {(IsServer ? "Player 1 (Host)" : "Player 2 (Client)")}");
    }

    private void Update()
    {
        if (!IsSpawned) return;

        // 1. EVERYONE updates their visuals based on synced network variables
        ApplyNetworkVisuals();

        // 2. Only the SERVER handles Player 1's rotation logic
        if (IsServer)
        {
            HandlePlayer1Rotation();
        }

        // 3. Only the CLIENT handles Player 2's input logic
        if (IsClient && !IsServer)
        {
            HandlePlayer2Inputs();
        }
    }

    /// <summary>
    /// Reads the synced NetworkVariables and updates visuals for BOTH host and client.
    /// </summary>
    private void ApplyNetworkVisuals()
    {
        // Apply leaning visualization on the X axis
        transform.localRotation = Quaternion.Euler(netCurrentLean.Value, transform.localRotation.eulerAngles.y, 0f);

        // Both host and client will now move their local leg cubes properly
        if (leftLegVisual != null)
            leftLegVisual.localPosition = new Vector3(leftLegVisual.localPosition.x, netLeftLegRaised.Value ? 0.5f : 0f, leftLegVisual.localPosition.z);

        if (rightLegVisual != null)
            rightLegVisual.localPosition = new Vector3(rightLegVisual.localPosition.x, netRightLegRaised.Value ? 0.5f : 0f, rightLegVisual.localPosition.z);
    }

    #region PLAYER 1 (SERVER / HOST) LOGIC
    /// <summary>
    /// Player 1 controls rotation directly on the server via A/D or Left/Right arrows.
    /// </summary>
    private void HandlePlayer1Rotation()
    {
        // Safety check to ensure a keyboard is connected
        if (Keyboard.current == null) return;

        float horizontalInput = 0f;

        // Read direct keyboard states instead of old Input.GetAxis
        if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) horizontalInput += 1f;
        if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) horizontalInput -= 1f;

        if (Mathf.Abs(horizontalInput) > 0.01f)
        {
            transform.Rotate(Vector3.up, horizontalInput * rotationSpeed * Time.deltaTime);
        }
    }

    /// <summary>
    /// Server reads the synced NetworkVariables and updates visuals/transforms.
    /// </summary>
    private void ApplyClientInputs()
    {
        // Apply leaning visualization on the X axis
        transform.localRotation = Quaternion.Euler(netCurrentLean.Value, transform.localRotation.eulerAngles.y, 0f);

        // Simple placeholder logic to visually lift legs up and down based on network state
        if (leftLegVisual != null)
            leftLegVisual.localPosition = new Vector3(leftLegVisual.localPosition.x, netLeftLegRaised.Value ? 0.5f : 0f, leftLegVisual.localPosition.z);

        if (rightLegVisual != null)
            rightLegVisual.localPosition = new Vector3(rightLegVisual.localPosition.x, netRightLegRaised.Value ? 0.5f : 0f, rightLegVisual.localPosition.z);
    }
    #endregion

    #region PLAYER 2 (CLIENT) LOGIC
    /// <summary>
    /// Player 2 captures input locally and passes it up to the server via RPCs.
    /// </summary>
    private void HandlePlayer2Inputs()
    {
        if (Keyboard.current == null) return;

        // 1. Handle Leaning Input (W/S keys or Up/Down arrows)
        float verticalInput = 0f;
        if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) verticalInput += 1f;
        if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) verticalInput -= 1f;

        if (Mathf.Abs(verticalInput) > 0.01f)
        {
            currentLeanAngle += verticalInput * leanSpeed * Time.deltaTime;
            currentLeanAngle = Mathf.Clamp(currentLeanAngle, -maxLeanAngle, maxLeanAngle);

            SubmitLeanServerRpc(currentLeanAngle);
        }

        // 2. Handle Leg Controls using frame triggers (Q for Left, E for Right)
        if (Keyboard.current.qKey.wasPressedThisFrame) SubmitLegLiftServerRpc(true, true);
        if (Keyboard.current.qKey.wasReleasedThisFrame) SubmitLegLiftServerRpc(true, false);

        if (Keyboard.current.eKey.wasPressedThisFrame) SubmitLegLiftServerRpc(false, true);
        if (Keyboard.current.eKey.wasReleasedThisFrame) SubmitLegLiftServerRpc(false, false);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitLeanServerRpc(float leanValue)
    {
        netCurrentLean.Value = leanValue;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitLegLiftServerRpc(bool isLeftLeg, bool isRaised)
    {
        if (isLeftLeg)
        {
            netLeftLegRaised.Value = isRaised;
            Debug.Log($"Server received: Left leg raised = {isRaised}");
        }
        else
        {
            netRightLegRaised.Value = isRaised;
            Debug.Log($"Server received: Right leg raised = {isRaised}");
        }
    }
    #endregion
}