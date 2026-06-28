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

    [Header("Rig Orientation Fix")]
    [Tooltip("Adjust this if the feet spawn backwards or flipped. Try 180 if they point away.")]
    [SerializeField] private float footForwardFlipCorrection = 180f;

    // Persistent world anchors used when a foot is planted on the ground
    private Vector3 leftPlantedWorldPos;
    private Quaternion leftPlantedWorldRot;
    private Vector3 rightPlantedWorldPos;
    private Quaternion rightPlantedWorldRot;

    // Design-time local home points relative to the root body
    private Vector3 leftFootHomeLocalPos;
    private Vector3 rightFootHomeLocalPos;

    // Synced states
    private NetworkVariable<bool> netLeftLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netRightLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netMoveInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netTurnInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Establish the baseline relative home positions
        leftFootHomeLocalPos = transform.InverseTransformPoint(leftFootTarget.position);
        rightFootHomeLocalPos = transform.InverseTransformPoint(rightFootTarget.position);

        // Initialize world tracking arrays
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

        // PLAYER 1: A/D Turning
        if (localRole == PlayerRole.Player1_Waist)
        {
            float turn = 0f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) turn += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) turn -= 1f;

            SubmitWaistRotationServerRpc(turn);
        }

        // PLAYER 2: W/S Stepping & Q/E Lifting
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
        // 1. Handle Turning (A/D)
        if (Mathf.Abs(netTurnInput.Value) > 0.01f)
        {
            float rotationAmount = netTurnInput.Value * rotationSpeed * Time.deltaTime;
            Vector3 pivotPoint = transform.position;

            transform.Rotate(Vector3.up, rotationAmount);

            // ROTATION FIX: Rotate the world anchor targets around the body center pivot on the server
            // so planted feet orbit with the body instead of tearing loose or snapping strangely
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

        // 2. Handle Forward/Backward Travel (W/S)
        if (Mathf.Abs(netMoveInput.Value) > 0.01f)
        {
            Vector3 moveDirection = transform.forward * netMoveInput.Value * moveSpeed * Time.deltaTime;
            transform.position += moveDirection;
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
        ResolveSingleLegKinematics(leftFootTarget, netLeftLegLifted.Value, leftFootHomeLocalPos, ref leftPlantedWorldPos, ref leftPlantedWorldRot);
        ResolveSingleLegKinematics(rightFootTarget, netRightLegLifted.Value, rightFootHomeLocalPos, ref rightPlantedWorldPos, ref rightPlantedWorldRot);
    }

    private void ResolveSingleLegKinematics(Transform footTarget, bool isLifted, Vector3 homeLocalPos, ref Vector3 plantedWorldPos, ref Quaternion plantedWorldRot)
    {
        // 1. HORIZONTAL TRACKING (X and Z Coordinates)
        if (isLifted)
        {
            // If actively lifted by Player 2, pull it forward smoothly toward its home tracking slot
            Vector3 worldHomePos = transform.TransformPoint(homeLocalPos);
            
            // Step position updates over time
            Vector3 currentXZ = Vector3.MoveTowards(
                new Vector3(footTarget.position.x, 0f, footTarget.position.z),
                new Vector3(worldHomePos.x, 0f, worldHomePos.z),
                liftSpeed * Time.deltaTime
            );

            // Reorient smoothly toward the movement vector plus our required rig fix factor
            Quaternion targetRotation = transform.rotation * Quaternion.Euler(0f, footForwardFlipCorrection, 0f);
            footTarget.rotation = Quaternion.Slerp(footTarget.rotation, targetRotation, liftSpeed * Time.deltaTime);

            // Keep updating our target anchor coordinates while in flight
            plantedWorldPos = new Vector3(currentXZ.x, footTarget.position.y, currentXZ.z);
            plantedWorldRot = footTarget.rotation;
        }
        else
        {
            // If planted, keep it strictly locked to its world target coordinates.
            // When turning, the server recalculates this position vector so it smoothly orbits.
            footTarget.rotation = plantedWorldRot;
        }

        // 2. VERTICAL HEIGHT SYSTEM (Y Axis Tracking - ALWAYS runs smoothly)
        Vector3 groundRayStart = new Vector3(plantedWorldPos.x, transform.position.y + 2f, plantedWorldPos.z);
        float targetFloorY = transform.position.y + homeLocalPos.y; // Fallback height profile

        if (Physics.Raycast(groundRayStart, Vector3.down, out RaycastHit hit, 5f, groundLayer))
        {
            targetFloorY = hit.point.y;
        }

        // Calculate the ideal height coordinate depending on whether the leg is lifted
        float finalTargetHeight = targetFloorY + (isLifted ? liftHeight : 0f);
        
        // Choose the speed based on whether the leg is traveling up or coming down
        float interpolationSpeed = isLifted ? liftSpeed : dropSpeed;
        float finalY = Mathf.MoveTowards(footTarget.position.y, finalTargetHeight, interpolationSpeed * Time.deltaTime);

        // Combine the decoupled horizontal target position with the calculated vertical ground height position
        footTarget.position = new Vector3(plantedWorldPos.x, finalY, plantedWorldPos.z);
    }
    #endregion
}