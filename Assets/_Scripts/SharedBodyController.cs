using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;
using UnityEngine.Animations.Rigging;

public class SharedBodyController : NetworkBehaviour
{
    public enum PlayerRole { Unassigned, Player1_Waist, Player2_Legs }
    
    [Header("Network Role")]
    public PlayerRole localRole = PlayerRole.Unassigned;

    [Header("Components")]
    [SerializeField] private Transform waistBone;
    [SerializeField] private RigBuilder rigBuilder;
    [SerializeField] private CapsuleCollider mainCollider;

    [Header("P1: Waist Rotation")]
    [SerializeField] private float rotationSpeed = 90f;

    [Header("P2: Waist Tilt & Legs")]
    [SerializeField] private float tiltSpeed = 45f;
    [SerializeField] private float maxTiltAngle = 35f;
    [Space]
    [SerializeField] private Transform leftFootTarget;
    [SerializeField] private Transform rightFootTarget;
    [SerializeField] private float liftHeight = 0.5f;
    [SerializeField] private float liftSpeed = 12f;
    [SerializeField] private LayerMask groundLayer;

    [Header("Balance & Fall Mechanics")]
    [SerializeField] private float maxBalanceDistance = 0.7f;
    [SerializeField] private float warningThreshold = 0.35f;

    private Rigidbody mainRb;
    private Vector3 leftFootHomeLocal;
    private Vector3 rightFootHomeLocal;

    // Synced Network States
    private NetworkVariable<float> netTurnInput = new NetworkVariable<float>(0f);
    private NetworkVariable<float> netTiltInput = new NetworkVariable<float>(0f);
    private NetworkVariable<bool> netLeftLifted = new NetworkVariable<bool>(false);
    private NetworkVariable<bool> netRightLifted = new NetworkVariable<bool>(false);
    private NetworkVariable<bool> netIsFallen = new NetworkVariable<bool>(false);

    private void Awake()
    {
        mainRb = GetComponent<Rigidbody>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Save local home layout positions
        leftFootHomeLocal = leftFootTarget.localPosition;
        rightFootHomeLocal = rightFootTarget.localPosition;

        // CRITICAL FIX: Make sure child ragdoll parts don't fight the main capsule during gameplay
        SetRagdollState(false);

        InitializeLocalPlayerRole();
        netIsFallen.OnValueChanged += OnFallStateChanged;
    }

    public override void OnNetworkDespawn()
    {
        netIsFallen.OnValueChanged -= OnFallStateChanged;
        base.OnNetworkDespawn();
    }

    // FIXED PROTECTION LEVEL: Accessible by CoopGameManager
    public void InitializeLocalPlayerRole()
    {
        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        var connectedClients = NetworkManager.Singleton.ConnectedClientsIds;

        if (connectedClients.Count >= 1 && connectedClients[0] == localClientId) localRole = PlayerRole.Player1_Waist;
        else if (connectedClients.Count >= 2 && connectedClients[1] == localClientId) localRole = PlayerRole.Player2_Legs;
    }

    private void Update()
    {
        if (!IsSpawned || netIsFallen.Value) return;

        HandleLocalInputs();
    }

    private void FixedUpdate()
    {
        if (!IsSpawned || !IsServer || netIsFallen.Value) return;

        // 1. Process Physics Turning Engine
        if (Mathf.Abs(netTurnInput.Value) > 0.01f)
        {
            float turn = netTurnInput.Value * rotationSpeed * Time.fixedDeltaTime;
            mainRb.MoveRotation(mainRb.rotation * Quaternion.Euler(0, turn, 0));
        }

        // 2. Process Server Balance Evaluation
        CheckServerBalance();
    }

    private void LateUpdate()
    {
        if (!IsSpawned || netIsFallen.Value) return;

        // Process procedural visuals and grounding constraints
        ProcessPuppetVisuals();
    }

    private void HandleLocalInputs()
    {
        if (Keyboard.current == null) return;

        if (localRole == PlayerRole.Unassigned)
        {
            localRole = IsServer ? PlayerRole.Player1_Waist : PlayerRole.Player2_Legs;
        }

        if (localRole == PlayerRole.Player1_Waist)
        {
            float turn = 0f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) turn += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) turn -= 1f;
            SubmitTurnServerRpc(turn);
        }

        if (localRole == PlayerRole.Player2_Legs)
        {
            float tilt = 0f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) tilt += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) tilt -= 1f;
            
            SubmitTiltAndLegsServerRpc(tilt, Keyboard.current.qKey.isPressed, Keyboard.current.eKey.isPressed);
        }
    }

    private void ProcessPuppetVisuals()
    {
        // 1. Smoothly apply Waist Leaning Pitch
        if (waistBone != null)
        {
            float targetXRotation = netTiltInput.Value * maxTiltAngle;
            waistBone.localRotation = Quaternion.Slerp(waistBone.localRotation, Quaternion.Euler(targetXRotation, 0, 0), tiltSpeed * Time.deltaTime);
        }

        // 2. Dynamic Floor Grounding Math
        PositionAndGroundFoot(leftFootTarget, leftFootHomeLocal, netLeftLifted.Value);
        PositionAndGroundFoot(rightFootTarget, rightFootHomeLocal, netRightLifted.Value);
    }

    private void PositionAndGroundFoot(Transform footTarget, Vector3 homeLocalPos, bool isLifted)
    {
        // Calculate where the foot naturally wants to be horizontally relative to the body
        Vector3 targetLocal = homeLocalPos;
        if (isLifted) targetLocal += Vector3.up * liftHeight;

        Vector3 desiredWorldPos = transform.TransformPoint(targetLocal);

        // Grounding System: Find the exact floor coordinate directly underneath this step point
        Vector3 rayStart = new Vector3(desiredWorldPos.x, transform.position.y + 1f, desiredWorldPos.z);
        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 4f, groundLayer))
        {
            float floorY = hit.point.y;
            desiredWorldPos.y = floorY + (isLifted ? liftHeight : 0f);
        }

        // Interpolate foot cleanly to prevent jitter snaps
        footTarget.position = Vector3.MoveTowards(footTarget.position, desiredWorldPos, liftSpeed * Time.deltaTime);
        footTarget.rotation = Quaternion.Slerp(footTarget.rotation, transform.rotation, liftSpeed * Time.deltaTime);
    }

    private void CheckServerBalance()
    {
        // Balanced if both feet are on the floor or both are raised
        if (netLeftLifted.Value == netRightLifted.Value) return;

        // Identify the support foot anchored to the ground plane
        Transform supportFoot = netLeftLifted.Value ? rightFootTarget : leftFootTarget;

        Vector3 flatBodyCenter = new Vector3(mainRb.position.x, 0f, mainRb.position.z);
        Vector3 flatSupportFoot = new Vector3(supportFoot.position.x, 0f, supportFoot.position.z);

        // Determine if center of gravity is stepping too far away
        float displacement = Vector3.Distance(flatBodyCenter, flatSupportFoot);

        if (displacement > maxBalanceDistance)
        {
            netIsFallen.Value = true;
        }
    }

    private void OnFallStateChanged(bool oldState, bool newState)
    {
        if (newState == true)
        {
            if (rigBuilder != null) rigBuilder.enabled = false;
            if (mainCollider != null) mainCollider.enabled = false;

            if (mainRb != null)
            {
                mainRb.constraints = RigidbodyConstraints.None;
                mainRb.AddForce(transform.forward * 3f + Vector3.up * 2f, ForceMode.Impulse);
            }

            SetRagdollState(true);
        }
    }

    private void SetRagdollState(bool activateRagdoll)
    {
        Rigidbody[] childRbs = GetComponentsInChildren<Rigidbody>(true);
        foreach (Rigidbody boneRb in childRbs)
        {
            if (boneRb == mainRb) continue;
            boneRb.isKinematic = !activateRagdoll;
            boneRb.useGravity = activateRagdoll;
        }
    }

    #region NETWORK PROTOCOLS
    [Rpc(SendTo.Server)] private void SubmitTurnServerRpc(float turn) => netTurnInput.Value = turn;

    [Rpc(SendTo.Server)]
    private void SubmitTiltAndLegsServerRpc(float tilt, bool liftLeft, bool liftRight)
    {
        netTiltInput.Value = tilt;
        netLeftLifted.Value = liftLeft;
        netRightLifted.Value = liftRight;
    }
    #endregion
}