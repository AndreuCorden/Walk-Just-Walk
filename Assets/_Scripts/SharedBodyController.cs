using System.Collections;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;

public class SharedBodyController : NetworkBehaviour
{
    public enum PlayerRole { Unassigned, Player1_Waist, Player2_Legs }
    
    [Header("Network Role")]
    public PlayerRole localRole = PlayerRole.Unassigned;

    [Header("Waist Movement Settings")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float rotationSpeed = 90f;

    [Header("IK Foot Targets")]
    [SerializeField] private Transform leftFootTarget;
    [SerializeField] private Transform rightFootTarget;

    [Header("Manual Step Settings")]
    [SerializeField] private float liftHeight = 0.5f;
    [SerializeField] private float dropSpeed = 10f;
    [SerializeField] private LayerMask groundLayer;

    private float leftDefaultY;
    private float rightDefaultY;

    // Sync variables using Server-authoritative writes
    private NetworkVariable<bool> netLeftLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netRightLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    // Inputs must be synced from Player 1 Client to Server via RPC
    private NetworkVariable<float> netMoveInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netTurnInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        leftDefaultY = leftFootTarget.localPosition.y; // Local coordinates are safer if body moves
        rightDefaultY = rightFootTarget.localPosition.y;
    }

    public void InitializeLocalPlayerRole()
    {
        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        
        // Simple logic: Server/Host or first client is Player 1, second client is Player 2
        var connectedClients = NetworkManager.Singleton.ConnectedClientsIds;
        
        if (connectedClients.Count >= 1 && connectedClients[0] == localClientId)
        {
            localRole = PlayerRole.Player1_Waist;
            Debug.Log("You are Player 1: Waist and Rotation Control.");
        }
        else if (connectedClients.Count >= 2 && connectedClients[1] == localClientId)
        {
            localRole = PlayerRole.Player2_Legs;
            Debug.Log("You are Player 2: Leg Lifter.");
        }
    }

    private void Update()
    {
        if (!IsSpawned) return;

        // 1. Gather local inputs based on assigned role and send them to server
        HandleLocalInputs();

        // 2. Only Server processes physical body movement using synced data
        if (IsServer)
        {
            ProcessServerMovement();
        }

        // 3. EVERYONE updates visual components (Leg lifting) based on NetworkVariables
        ProcessFootHeights();
    }

    private void HandleLocalInputs()
    {
        if (Keyboard.current == null) return;

        // PLAYER 1 INPUTS (Waist)
        if (localRole == PlayerRole.Player1_Waist)
        {
            float turn = 0f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) turn += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) turn -= 1f;

            float move = 0f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) move += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) move -= 1f;

            SubmitWaistInputServerRpc(move, turn);
        }

        // PLAYER 2 INPUTS (Legs)
        if (localRole == PlayerRole.Player2_Legs)
        {
            if (Keyboard.current.qKey.wasPressedThisFrame) SubmitLegStateServerRpc(true, true);
            if (Keyboard.current.qKey.wasReleasedThisFrame) SubmitLegStateServerRpc(true, false);

            if (Keyboard.current.eKey.wasPressedThisFrame) SubmitLegStateServerRpc(false, true);
            if (Keyboard.current.eKey.wasReleasedThisFrame) SubmitLegStateServerRpc(false, false);
        }
    }

    #region SERVER PHYSICS AND MOVEMENT Processing
    private void ProcessServerMovement()
    {
        // Apply rotation from synced network input
        if (Mathf.Abs(netTurnInput.Value) > 0.01f)
        {
            transform.Rotate(Vector3.up, netTurnInput.Value * rotationSpeed * Time.deltaTime);
        }

        // Apply forward/back movement from synced network input
        if (Mathf.Abs(netMoveInput.Value) > 0.01f)
        {
            transform.position += transform.forward * netMoveInput.Value * moveSpeed * Time.deltaTime;
        }
    }
    #endregion

    #region RPCS (INPUT SUBMISSIONS TO SERVER)
    [Rpc(SendTo.Server)]
    private void SubmitWaistInputServerRpc(float move, float turn)
    {
        netMoveInput.Value = move;
        netTurnInput.Value = turn;
    }

    [Rpc(SendTo.Server)]
    private void SubmitLegStateServerRpc(bool isLeftLeg, bool isLifted)
    {
        if (isLeftLeg) netLeftLegLifted.Value = isLifted;
        else netRightLegLifted.Value = isLifted;
    }
    #endregion

    #region VISUALS (EVERYONE)
    private void ProcessFootHeights()
    {
        ProcessSingleFoot(leftFootTarget, netLeftLegLifted.Value, leftDefaultY);
        ProcessSingleFoot(rightFootTarget, netRightLegLifted.Value, rightDefaultY);
    }

    private void ProcessSingleFoot(Transform footTarget, bool isLifted, float defaultY)
    {
        if (isLifted)
        {
            float targetLiftHeight = transform.position.y + liftHeight;
            footTarget.position = new Vector3(footTarget.position.x, targetLiftHeight, footTarget.position.z);
        }
        else
        {
            Vector3 rayStart = new Vector3(footTarget.position.x, transform.position.y + 2f, footTarget.position.z);
            float targetY = transform.position.y + defaultY; // Adjusted to be relative to the body's actual current height

            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 5f, groundLayer))
            {
                targetY = hit.point.y;
            }

            if (footTarget.position.y > targetY)
            {
                float newY = Mathf.MoveTowards(footTarget.position.y, targetY, dropSpeed * Time.deltaTime);
                footTarget.position = new Vector3(footTarget.position.x, newY, footTarget.position.z);
            }
        }
    }
    #endregion
}