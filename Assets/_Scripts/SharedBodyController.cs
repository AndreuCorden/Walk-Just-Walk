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
    [SerializeField] private float liftHeight = 0.3f; 
    [SerializeField] private float liftSpeed = 8f;     
    [SerializeField] private float dropSpeed = 10f;
    [SerializeField] private LayerMask groundLayer;

    private float leftDefaultY;
    private float rightDefaultY;

    // Track the exact world position where a foot was planted
    private Vector3 leftPlantedWorldPos;
    private Vector3 rightPlantedWorldPos;

    // Sync variables using Server-authoritative writes
    private NetworkVariable<bool> netLeftLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netRightLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    // Inputs synced from clients to Server via RPC
    private NetworkVariable<float> netMoveInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netTurnInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        leftDefaultY = leftFootTarget.localPosition.y; 
        rightDefaultY = rightFootTarget.localPosition.y;

        // Initialize baseline positions
        leftPlantedWorldPos = leftFootTarget.position;
        rightPlantedWorldPos = rightFootTarget.position;

        InitializeLocalPlayerRole();
    }

    public void InitializeLocalPlayerRole()
    {
        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        var connectedClients = NetworkManager.Singleton.ConnectedClientsIds;
        
        if (connectedClients.Count >= 1 && connectedClients[0] == localClientId)
        {
            localRole = PlayerRole.Player1_Waist;
        }
        else if (connectedClients.Count >= 2 && connectedClients[1] == localClientId)
        {
            localRole = PlayerRole.Player2_Legs;
        }
    }

    private void Update()
    {
        if (!IsSpawned) return;

        HandleLocalInputs();

        if (IsServer)
        {
            ProcessServerMovement();
        }

        ProcessFootHeights();
    }

    private void HandleLocalInputs()
    {
        if (Keyboard.current == null) return;

        if (localRole == PlayerRole.Unassigned)
        {
            if (IsServer) localRole = PlayerRole.Player1_Waist;
            else localRole = PlayerRole.Player2_Legs;
        }

        // PLAYER 1: Waist Controls (A/D to Pivot)
        if (localRole == PlayerRole.Player1_Waist)
        {
            float turn = 0f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) turn += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) turn -= 1f;

            SubmitWaistRotationServerRpc(turn);
        }

        // PLAYER 2: Leg Controls (W/S to Move, Q/E to Lift)
        if (localRole == PlayerRole.Player2_Legs)
        {
            float move = 0f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) move += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) move -= 1f;

            SubmitLegMovementServerRpc(move);

            if (Keyboard.current.qKey.wasPressedThisFrame) SubmitLegStateServerRpc(true, true);
            if (Keyboard.current.qKey.wasReleasedThisFrame) SubmitLegStateServerRpc(true, false);

            if (Keyboard.current.eKey.wasPressedThisFrame) SubmitLegStateServerRpc(false, true);
            if (Keyboard.current.eKey.wasReleasedThisFrame) SubmitLegStateServerRpc(false, false);
        }
    }

    #region SERVER PHYSICS AND MOVEMENT
    private void ProcessServerMovement()
    {
        // 1. Player 1 turns the core body object
        if (Mathf.Abs(netTurnInput.Value) > 0.01f)
        {
            transform.Rotate(Vector3.up, netTurnInput.Value * rotationSpeed * Time.deltaTime);
        }

        // 2. Player 2 translates the entire core body object forward/backward
        if (Mathf.Abs(netMoveInput.Value) > 0.01f)
        {
            Vector3 moveDirection = transform.forward * netMoveInput.Value * moveSpeed * Time.deltaTime;
            transform.position += moveDirection;

            // If a leg is LIFTED, its world position target rides forward through space perfectly with the body
            if (netLeftLegLifted.Value)
            {
                leftPlantedWorldPos += moveDirection;
            }
            if (netRightLegLifted.Value)
            {
                rightPlantedWorldPos += moveDirection;
            }
            // Grounded feet get NO modification, making them drop backwards relative to the hips!
        }
    }
    #endregion

    #region RPCS
    [Rpc(SendTo.Server)]
    private void SubmitWaistRotationServerRpc(float turn)
    {
        netTurnInput.Value = turn;
    }

    [Rpc(SendTo.Server)]
    private void SubmitLegMovementServerRpc(float move)
    {
        netMoveInput.Value = move;
    }

    [Rpc(SendTo.Server)]
    private void SubmitLegStateServerRpc(bool isLeftLeg, bool isLifted)
    {
        if (isLeftLeg) netLeftLegLifted.Value = isLifted;
        else netRightLegLifted.Value = isLifted;

        // Snapping baseline when states toggle
        if (IsServer)
        {
            if (isLeftLeg) leftPlantedWorldPos = leftFootTarget.position;
            else rightPlantedWorldPos = rightFootTarget.position;
        }
    }
    #endregion

    #region VISUAL FOOT SNAP PROCESSING (EVERYONE)
    private void ProcessFootHeights()
    {
        ProcessSingleFoot(leftFootTarget, netLeftLegLifted.Value, leftDefaultY, ref leftPlantedWorldPos);
        ProcessSingleFoot(rightFootTarget, netRightLegLifted.Value, rightDefaultY, ref rightPlantedWorldPos);
    }

    private void ProcessSingleFoot(Transform footTarget, bool isLifted, float defaultY, ref Vector3 plantedWorldPos)
    {
        // Calculate where the floor layout surface is right under the foot's tracked coordinate position
        Vector3 rayStart = new Vector3(plantedWorldPos.x, transform.position.y + 2f, plantedWorldPos.z);
        float floorY = transform.position.y + defaultY; 

        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 5f, groundLayer))
        {
            floorY = hit.point.y;
        }

        if (isLifted)
        {
            // Smoothly lift up over the floor height
            float targetLiftHeight = floorY + liftHeight;
            float newY = Mathf.MoveTowards(footTarget.position.y, targetLiftHeight, liftSpeed * Time.deltaTime);
            
            // Keep X and Z moving smoothly alongside the updated forward tracking reference position
            footTarget.position = new Vector3(plantedWorldPos.x, newY, plantedWorldPos.z);
        }
        else
        {
            // Lock foot directly down onto the targeted absolute anchor position coordinates
            float newY = Mathf.MoveTowards(footTarget.position.y, floorY, dropSpeed * Time.deltaTime);
            footTarget.position = new Vector3(plantedWorldPos.x, newY, plantedWorldPos.z);
            
            // Update reference tracking position
            plantedWorldPos = footTarget.position;
        }
    }
    #endregion
}