using System.Collections;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;
using UnityEngine.Animations.Rigging;

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

    [Header("IK Arm Targets (Balance Warning)")]
    [SerializeField] private Transform leftArmTarget;
    [SerializeField] private Transform rightArmTarget;
    [SerializeField] private RigBuilder rigBuilder;

    [Header("Manual Step Settings")]
    [SerializeField] private float liftHeight = 0.4f;
    [SerializeField] private float liftSpeed = 12f;
    [SerializeField] private float dropSpeed = 15f;
    [SerializeField] private LayerMask groundLayer;

    [Header("Advanced Balance Mechanics")]
    [Tooltip("Maximum horizontal distance the body can move away from the support foot before falling.")]
    [SerializeField] private float maxBalanceDistance = 0.8f;
    [Tooltip("Distance split from the foot where arms begin spreading and shaking.")]
    [SerializeField] private float warningThreshold = 0.4f;
    [Tooltip("How far outward the arms expand when completely off balance.")]
    [SerializeField] private float armSpreadDistance = 0.7f;
    [Tooltip("How violent the arm shaking is right before falling.")]
    [SerializeField] private float shakeIntensity = 0.08f;

    // Persistent world anchors for grounded feet
    private Vector3 leftPlantedWorldPos;
    private Quaternion leftPlantedWorldRot;
    private Vector3 rightPlantedWorldPos;
    private Quaternion rightPlantedWorldRot;

    // Rig defaults captured at spawn time
    private Vector3 leftFootHomeLocalPos;
    private Quaternion leftFootHomeLocalRot;
    private Vector3 rightFootHomeLocalPos;
    private Quaternion rightFootHomeLocalRot;

    private Vector3 leftArmHomeLocalPos;
    private Vector3 rightArmHomeLocalPos;

    private Rigidbody rb;

    // Synced Netcode states
    private NetworkVariable<bool> netLeftLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netRightLegLifted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netMoveInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netTurnInput = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Synced balance variables
    private NetworkVariable<bool> netIsFallen = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netBalanceWarningFactor = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private Vector3 serverFallDirection;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Capture default structural matrices
        leftFootHomeLocalPos = transform.InverseTransformPoint(leftFootTarget.position);
        leftFootHomeLocalRot = Quaternion.Inverse(transform.rotation) * leftFootTarget.rotation;
        rightFootHomeLocalPos = transform.InverseTransformPoint(rightFootTarget.position);
        rightFootHomeLocalRot = Quaternion.Inverse(transform.rotation) * rightFootTarget.rotation;

        if (leftArmTarget != null) leftArmHomeLocalPos = leftArmTarget.localPosition;
        if (rightArmTarget != null) rightArmHomeLocalPos = rightArmTarget.localPosition;

        leftPlantedWorldPos = leftFootTarget.position;
        leftPlantedWorldRot = leftFootTarget.rotation;
        rightPlantedWorldPos = rightFootTarget.position;
        rightPlantedWorldRot = rightFootTarget.rotation;

        InitializeLocalPlayerRole();

        // Listen for fall state changes to toggle physical configurations globally
        netIsFallen.OnValueChanged += OnFallStateChanged;
    }

    public override void OnNetworkDespawn()
    {
        netIsFallen.OnValueChanged -= OnFallStateChanged;
        base.OnNetworkDespawn();
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

        if (netIsFallen.Value) return; // If fallen, physics engine handles entirely

        HandleLocalInputs();

        if (IsServer)
        {
            ProcessServerMovement();
            CheckServerBalance();
        }
    }

    private void LateUpdate()
    {
        if (!IsSpawned || netIsFallen.Value) return;

        ProcessProceduralLegBends();
        ProcessProceduralArmShaking();
    }

    private void HandleLocalInputs()
    {
        if (Keyboard.current == null) return;

        if (localRole == PlayerRole.Unassigned)
        {
            if (IsServer) localRole = PlayerRole.Player1_Waist;
            else localRole = PlayerRole.Player2_Legs;
        }

        if (localRole == PlayerRole.Player1_Waist)
        {
            float turn = 0f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) turn += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) turn -= 1f;

            SubmitWaistRotationServerRpc(turn);
        }

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

    #region SERVER PHYSICS & BALANCE CONTROLLER
    private void ProcessServerMovement()
    {
        if (Mathf.Abs(netTurnInput.Value) > 0.01f)
        {
            float rotationAmount = netTurnInput.Value * rotationSpeed * Time.deltaTime;
            Vector3 pivotPoint = transform.position;

            transform.Rotate(Vector3.up, rotationAmount);

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

        if (Mathf.Abs(netMoveInput.Value) > 0.01f)
        {
            if (netLeftLegLifted.Value || netRightLegLifted.Value)
            {
                Vector3 moveDirection = transform.forward * netMoveInput.Value * moveSpeed * Time.deltaTime;
                transform.position += moveDirection;
            }
        }
    }

    private void CheckServerBalance()
    {
        // Balanced if feet are in matching states
        if (netLeftLegLifted.Value == netRightLegLifted.Value)
        {
            netBalanceWarningFactor.Value = Mathf.MoveTowards(netBalanceWarningFactor.Value, 0f, Time.deltaTime * 2f);
            return;
        }

        Vector3 supportFootPos = netLeftLegLifted.Value ? rightPlantedWorldPos : leftPlantedWorldPos;
        Vector3 flatBodyCenter = new Vector3(transform.position.x, 0f, transform.position.z);
        Vector3 flatSupportFoot = new Vector3(supportFootPos.x, 0f, supportFootPos.z);

        float horizontalDisplacement = Vector3.Distance(flatBodyCenter, flatSupportFoot);

        // 1. Calculate Warning Factor (0 to 1) based on current lean limits
        if (horizontalDisplacement > warningThreshold)
        {
            float range = maxBalanceDistance - warningThreshold;
            netBalanceWarningFactor.Value = Mathf.Clamp01((horizontalDisplacement - warningThreshold) / range);
        }
        else
        {
            netBalanceWarningFactor.Value = Mathf.MoveTowards(netBalanceWarningFactor.Value, 0f, Time.deltaTime * 2f);
        }

        // 2. CRITICAL TIP-OVER CHECK
        if (horizontalDisplacement > maxBalanceDistance)
        {
            serverFallDirection = transform.forward * (netMoveInput.Value >= 0f ? 1f : -1f);
            if (serverFallDirection == Vector3.zero) serverFallDirection = transform.forward;

            netIsFallen.Value = true;
        }
    }
    #endregion

    #region PHYSICAL FALL HANDLING
    private void OnFallStateChanged(bool oldState, bool newState)
    {
        if (newState == true)
        {
            // Turn off the animation rig so bones collapse freely under physics
            if (rigBuilder != null) rigBuilder.enabled = false;

            // Find EVERY child bone collider/rigidbody created by the ragdoll wizard and wake them up!
            Rigidbody[] childRbs = GetComponentsInChildren<Rigidbody>(true);
            foreach (Rigidbody boneRb in childRbs)
            {
                boneRb.isKinematic = false;
                boneRb.useGravity = true;

                // Add an organic push to the bones to make the fall look violent
                if (IsServer)
                {
                    boneRb.AddForce(serverFallDirection * 5f, ForceMode.Impulse);
                }
            }
        }
    }    
    #endregion

    #region PROCEDURAL ARM SHAKING VISUALS
    private void ProcessProceduralArmShaking()
    {
        if (leftArmTarget == null || rightArmTarget == null) return;

        float warningFactor = netBalanceWarningFactor.Value;

        if (warningFactor > 0.01f)
        {
            int armShakeSpeed = 1;
            // Calculate a high-frequency noise shake using Sine waves and time variables
            float shakeOffset = Mathf.Sin(Time.time * armShakeSpeed) * shakeIntensity * warningFactor;

            // Displace targets horizontally outward (Left goes negative X, Right goes positive X)
            Vector3 leftTargetPos = leftArmHomeLocalPos + new Vector3(-armSpreadDistance * warningFactor, armSpreadDistance * 0.5f * warningFactor, 0f);
            Vector3 rightTargetPos = rightArmHomeLocalPos + new Vector3(armSpreadDistance * warningFactor, armSpreadDistance * 0.5f * warningFactor, 0f);

            // Add shake jitter directly onto the targets
            leftArmTarget.localPosition = leftTargetPos + new Vector3(0f, shakeOffset, Random.Range(-shakeOffset, shakeOffset) * 0.5f);
            rightArmTarget.localPosition = rightTargetPos + new Vector3(0f, -shakeOffset, Random.Range(-shakeOffset, shakeOffset) * 0.5f);
        }
        else
        {
            // Return smoothly to normal resting posture when safely balanced
            leftArmTarget.localPosition = Vector3.MoveTowards(leftArmTarget.localPosition, leftArmHomeLocalPos, Time.deltaTime * 4f);
            rightArmTarget.localPosition = Vector3.MoveTowards(rightArmTarget.localPosition, rightArmHomeLocalPos, Time.deltaTime * 4f);
        }
    }
    #endregion

    #region RPCS
    [Rpc(SendTo.Server)] private void SubmitWaistRotationServerRpc(float turn) { if (!netIsFallen.Value) netTurnInput.Value = turn; }
    [Rpc(SendTo.Server)] private void SubmitLegMovementServerRpc(float move) { if (!netIsFallen.Value) netMoveInput.Value = move; }
    [Rpc(SendTo.Server)]
    private void SubmitLegStateServerRpc(bool isLeftLeg, bool isLifted)
    {
        if (netIsFallen.Value) return;
        if (isLeftLeg) netLeftLegLifted.Value = isLifted;
        else netRightLegLifted.Value = isLifted;
    }
    #endregion

    #region PROCEDURAL LEG KINEMATICS
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