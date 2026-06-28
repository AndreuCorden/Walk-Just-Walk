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
    [SerializeField] private float liftHeight = 0.4f;
    [SerializeField] private float liftSpeed = 12f;
    [SerializeField] private float dropSpeed = 15f;
    [SerializeField] private LayerMask groundLayer;

    // Persistent world anchors for the grounded state
    private Vector3 leftPlantedWorldPos;
    private Quaternion leftPlantedWorldRot;
    private Vector3 rightPlantedWorldPos;
    private Quaternion rightPlantedWorldRot;

    // Rig architectural defaults captured at runtime spawn
    private Vector3 leftFootHomeLocalPos;
    private Quaternion leftFootHomeLocalRot;
    private Vector3 rightFootHomeLocalPos;
    private Quaternion rightFootHomeLocalRot;

    // Synced Netcode states
    private NetworkVariable<bool> netLeftLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netRightLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netMoveInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netTurnInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Capture EXACT offsets and structural rotations relative to the character root
        leftFootHomeLocalPos = transform.InverseTransformPoint(leftFootTarget.position);
        leftFootHomeLocalRot = Quaternion.Inverse(transform.rotation) * leftFootTarget.rotation;

        rightFootHomeLocalPos = transform.InverseTransformPoint(rightFootTarget.position);
        rightFootHomeLocalRot = Quaternion.Inverse(transform.rotation) * rightFootTarget.rotation;

        // Set up baseline world tracking vectors
        leftPlantedWorldPos = leftFootTarget.position;
        leftPlantedWorldRot = leftFootTarget.rotation;
        rightPlantedWorldPos = rightFootTarget.position;
        rightPlantedWorldRot = rightFootTarget.rotation;

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
    }

    private void LateUpdate()
    {
        if (!IsSpawned) return;
        ProcessProceduralLegBends();
    }

    private void HandleLocalInputs()
    {
        if (Keyboard.current == null) return;

        if (localRole == PlayerRole.Unassigned)
        {
            if (IsServer) localRole = PlayerRole.Player1_Waist;
            else localRole = PlayerRole.Player2_Legs;
        }

        // PLAYER 1: Waist Controls (A/D)
        if (localRole == PlayerRole.Player1_Waist)
        {
            float turn = 0f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) turn += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) turn -= 1f;

            SubmitWaistRotationServerRpc(turn);
        }

        // PLAYER 2: Leg Controls (W/S and Q/E)
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

    #region SERVER PHYSICS
    private void ProcessServerMovement()
    {
        // 1. Handle Turning (A/D) - Always allowed so players can look around while stationary
        if (Mathf.Abs(netTurnInput.Value) > 0.01f)
        {
            float rotationAmount = netTurnInput.Value * rotationSpeed * Time.deltaTime;
            Vector3 pivotPoint = transform.position;

            transform.Rotate(Vector3.up, rotationAmount);

            // Rotate world anchors smoothly around the player's pivot point
            if (!netLeftLegLifted.Value)
            {
                leftPlantedWorldPos = Quaternion.Euler(0f, rotationAmount, 0f) * (leftPlantedWorldPos - pivotPoint) + pivotPoint;
                leftPlantedWorldRot = Quaternion.Euler(0f, rotationAmount, 0f) * leftPlantedWorldRot;
            }
            if (!netRightLegLifted.Value)
            {
                rightPlantedWorldPos = Quaternion.Euler(0f, rotationAmount, 0f) * (rightPlantedWorldPos - pivotPoint) + pivotPoint;
                rightPlantedWorldRot = Quaternion.Euler(0f, rotationAmount, 0f) * rightPlantedWorldRot;
            }
        }

        // 2. FINAL CONDITION FIX: Handle Forward/Backward Travel (W/S)
        // ONLY translate position if Player 2 is actively lifting at least one leg (Q or E)
        if (Mathf.Abs(netMoveInput.Value) > 0.01f)
        {
            bool isAnyLegLifted = netLeftLegLifted.Value || netRightLegLifted.Value;

            if (isAnyLegLifted)
            {
                Vector3 moveDirection = transform.forward * netMoveInput.Value * moveSpeed * Time.deltaTime;
                transform.position += moveDirection;
            }
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
    }
    #endregion

    #region VISUAL PROCEDURAL KINEMATICS
    private void ProcessProceduralLegBends()
    {
        ResolveSingleLegKinematics(leftFootTarget, netLeftLegLifted.Value, leftFootHomeLocalPos, leftFootHomeLocalRot, ref leftPlantedWorldPos, ref leftPlantedWorldRot);
        ResolveSingleLegKinematics(rightFootTarget, netRightLegLifted.Value, rightFootHomeLocalPos, rightFootHomeLocalRot, ref rightPlantedWorldPos, ref rightPlantedWorldRot);
    }

    private void ResolveSingleLegKinematics(Transform footTarget, bool isLifted, Vector3 homeLocalPos, Quaternion homeLocalRot, ref Vector3 plantedWorldPos, ref Quaternion plantedWorldRot)
    {
        Vector3 worldHomePos = transform.TransformPoint(homeLocalPos);
        Quaternion worldHomeRot = transform.rotation * homeLocalRot;

        Vector3 groundRayStart = new Vector3(isLifted ? worldHomePos.x : plantedWorldPos.x, transform.position.y + 2f, isLifted ? worldHomePos.z : plantedWorldPos.z);
        float targetFloorY = worldHomePos.y;

        if (Physics.Raycast(groundRayStart, Vector3.down, out RaycastHit hit, 5f, groundLayer))
        {
            targetFloorY = hit.point.y;
        }

        // 1. HORIZONTAL & ROTATIONAL TRACKING
        if (isLifted)
        {
            Vector3 currentXZ = Vector3.MoveTowards(
                new Vector3(footTarget.position.x, 0f, footTarget.position.z),
                new Vector3(worldHomePos.x, 0f, worldHomePos.z),
                liftSpeed * Time.deltaTime
            );

            footTarget.rotation = Quaternion.Slerp(footTarget.rotation, worldHomeRot, liftSpeed * Time.deltaTime);

            plantedWorldPos = new Vector3(currentXZ.x, footTarget.position.y, currentXZ.z);
            plantedWorldRot = footTarget.rotation;
        }
        else
        {
            footTarget.rotation = plantedWorldRot;
        }

        // 2. VERTICAL HEIGHT SYSTEM
        float finalTargetHeight = targetFloorY + (isLifted ? liftHeight : 0f);
        float interpolationSpeed = isLifted ? liftSpeed : dropSpeed;
        float finalY = Mathf.MoveTowards(footTarget.position.y, finalTargetHeight, interpolationSpeed * Time.deltaTime);

        footTarget.position = new Vector3(plantedWorldPos.x, finalY, plantedWorldPos.z);

        if (!isLifted)
        {
            plantedWorldPos = new Vector3(footTarget.position.x, finalY, footTarget.position.z);
        }
    }
    #endregion
}