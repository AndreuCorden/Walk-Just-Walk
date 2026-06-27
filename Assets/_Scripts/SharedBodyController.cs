using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;

/// <summary>
/// Project: Shared Stride (Active Ragdoll Transition)
/// Core Hook: Asymmetric, cooperative multiplayer where two players control a single physical body.
/// Player 1 (Server/Host): Controls overall world orientation (pivoting what forward is).
/// Player 2 (Client): Controls upper-body leaning and programmatic leg stepping mechanics.
/// </summary>
public class SharedBodyController : NetworkBehaviour
{
    [Header("Network Test Bench")]
    [Tooltip("Set to false when running multi-instance MPPM network tests. Set to true if testing keys solo in the editor.")]
    [SerializeField] private bool enableLocalTesting = false;

    [Header("Vertical Suspension Physics")]
    [Tooltip("The ideal target height (in meters) the hips should hover above the ground floor.")]
    [SerializeField] private float targetStandingHeight = 1.0f;
    [Tooltip("The upward spring force used to lift the character's physical mass against gravity.")]
    [SerializeField] private float suspensionSpring = 1500f;
    [Tooltip("Friction applied to the vertical spring to prevent pogo-stick bouncing or stuttering.")]
    [SerializeField] private float suspensionDamper = 80f;

    [Header("Rotational Muscle Physics")]
    [SerializeField] private float rotationSpeed = 90f;        // Player 1 horizontal turning rate
    [SerializeField] private float leanSpeed = 40f;            // Player 2 forward/backward leaning rate
    [SerializeField] private float maxLeanAngle = 45f;         // Max angular limit for spine leaning
    [SerializeField] private float balanceSpring = 600f;       // Strength keeping the spine and hips balanced
    [SerializeField] private float balanceDamper = 30f;        // Friction preventing limb wobbling/shaking

    [Header("Physical Rigidbody References")]
    [SerializeField] private Rigidbody hipsRigidbody;          // The 'Hips' root bone Rigidbody
    [SerializeField] private Rigidbody spineRigidbody;         // The 'Spine' bone Rigidbody
    [SerializeField] private Rigidbody leftThighRigidbody;     // The 'LeftUpLeg' bone Rigidbody
    [SerializeField] private Rigidbody rightThighRigidbody;    // The 'RightUpLeg' bone Rigidbody

    [Header("Leg Step Mechanics")]
    [SerializeField] private float legLiftAngle = 35f;         // Angle (in degrees) the thigh swings forward when stepping
    [SerializeField] private float legSpring = 300f;           // Rotational torque used to execute the step force

    // Synchronized multiplayer network variables (Data flows from Server down to all Clients)
    private NetworkVariable<float> netRootRotationY = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netCurrentLean = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netLeftLegRaised = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netRightLegRaised = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private float currentLeanAngle = 0f;
    private float currentRootRotationY = 0f;

    /// <summary>
    /// Awake executes the absolute frame this object enters the active scene layout.
    /// </summary>
    private void Awake()
    {
        // AUTOMATIC VIBRATION FIX: Tells all internal limb colliders to ignore each other, 
        // stopping the violent jittering caused by bones clipping into neighbor joints.
        DisableInternalCollisions();
    }

    /// <summary>
    /// Loops through every collider component inside the character skeleton hierarchy and disables mutual collisions.
    /// </summary>
    private void DisableInternalCollisions()
    {
        Collider[] allColliders = GetComponentsInChildren<Collider>();

        for (int i = 0; i < allColliders.Length; i++)
        {
            for (int j = i + 1; j < allColliders.Length; j++)
            {
                Physics.IgnoreCollision(allColliders[i], allColliders[j], true);
            }
        }
    }

    /// <summary>
    /// Executed automatically when this shared scene entity goes online across the Netcode transport layer.
    /// </summary>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Cache the default startup orientation of the pre-placed scene character
        currentRootRotationY = transform.eulerAngles.y;
    }

    private void Update()
    {
        // If local testing is disabled and the network session isn't online yet, skip input tracking
        if (!enableLocalTesting && !IsSpawned) return;

        // Player 1 (The Eyes / Host Machine) updates world rotation/steering mechanics
        if (enableLocalTesting || IsServer) 
        {
            HandlePlayer1Rotation();
        }

        // Player 2 (The Legs / Client Machine) tracks input updates locally
        if (enableLocalTesting || (IsClient && !IsServer)) 
        {
            HandlePlayer2Inputs();
        }
    }

    private void FixedUpdate()
    {
        // Safety exit: do nothing if the core physical bone components are missing
        if (hipsRigidbody == null) return;

        // Do not simulate active ragdoll balance forces if local testing is off and nobody is hosting yet
        if (!enableLocalTesting && !IsSpawned) return;

        // Calculate and apply active ragdoll forces
        ApplyActiveRagdollForces();
    }

    /// <summary>
    /// Handles the dual linear suspension springs and angular muscle torques to balance the single shared entity.
    /// </summary>
    private void ApplyActiveRagdollForces()
    {
        // --------------------------------------------------------------------------------
        // 1. THE LIFT SUSPENSION SPRING (Counters Gravity)
        // --------------------------------------------------------------------------------
        float currentHipHeight = hipsRigidbody.position.y;
        float heightError = targetStandingHeight - currentHipHeight;
        float verticalVelocity = hipsRigidbody.linearVelocity.y; // Unity 6 standard physics property

        // Linear Hooke's Law formula: (Gap * Stiffness) - (Velocity * Friction)
        // Keeps the hips hovering smoothly over the floor plane without crushing down
        float upwardForce = (heightError * suspensionSpring) - (verticalVelocity * suspensionDamper);

        // Prevent the spring from pulling the character down if they are tossed high into the air
        if (upwardForce < 0f) upwardForce = 0f;
        hipsRigidbody.AddForce(Vector3.up * upwardForce, ForceMode.Acceleration);

        // Read the synchronized target heading controlled by Player 1
        float activeHeadingY = enableLocalTesting ? currentRootRotationY : netRootRotationY.Value;

        // --------------------------------------------------------------------------------
        // 2. ROTATIONAL MUSCLE BALANCERS (Maintains Body Posture)
        // --------------------------------------------------------------------------------
        // HIP BASELINE BALANCER: Coordinates the hip orientation with the master horizontal look direction
        Quaternion targetHipRotation = Quaternion.Euler(0f, activeHeadingY, 0f);
        ApplyMuscleTorque(hipsRigidbody, targetHipRotation, balanceSpring, balanceDamper);

        // SPINE LEAN DRIVER: Bends the upper torso forward or backward based on Player 2's inputs
        if (spineRigidbody != null)
        {
            float targetLean = enableLocalTesting ? currentLeanAngle : netCurrentLean.Value;
            Quaternion targetSpineRotation = Quaternion.Euler(targetLean, activeHeadingY, 0f);
            ApplyMuscleTorque(spineRigidbody, targetSpineRotation, balanceSpring, balanceDamper);
        }

        // LEFT LEG STEP DRIVER: Swings the upper left leg joint forward when the step flag is active
        if (leftThighRigidbody != null)
        {
            bool isRaised = enableLocalTesting ? Keyboard.current.qKey.isPressed : netLeftLegRaised.Value;
            float targetX = isRaised ? -legLiftAngle : 0f; // Negative local angles swing the bone forward
            Quaternion targetLeftLegRot = Quaternion.Euler(targetX, activeHeadingY, 0f);
            ApplyMuscleTorque(leftThighRigidbody, targetLeftLegRot, legSpring, balanceDamper);
        }

        // RIGHT LEG STEP DRIVER: Swings the upper right leg joint forward when the step flag is active
        if (rightThighRigidbody != null)
        {
            bool isRaised = enableLocalTesting ? Keyboard.current.eKey.isPressed : netRightLegRaised.Value;
            float targetX = isRaised ? -legLiftAngle : 0f;
            Quaternion targetRightLegRot = Quaternion.Euler(targetX, activeHeadingY, 0f);
            ApplyMuscleTorque(rightThighRigidbody, targetRightLegRot, legSpring, balanceDamper);
        }
    }

    /// <summary>
    /// Computes the precise angular error gap between a limb's current rotation and its target orientation,
    /// then converts that gap into a corrective physics torque.
    /// </summary>
    private void ApplyMuscleTorque(Rigidbody rb, Quaternion targetRotation, float spring, float damper)
    {
        Quaternion deltaRotation = targetRotation * Quaternion.Inverse(rb.rotation);
        deltaRotation.ToAngleAxis(out float angle, out Vector3 axis);

        // Keep the rotation delta within a clean, standardized -180 to 180 degree operation window
        if (angle > 180f) angle -= 360f;

        if (Mathf.Abs(angle) > 0.01f)
        {
            // Angular velocity tracking to apply proportional torque damping forces
            Vector3 torqueForce = axis * (angle * spring) - (rb.angularVelocity * damper);
            rb.AddTorque(torqueForce, ForceMode.Acceleration);
        }
    }

    #region PLAYER INPUT MECHANICS
    private void HandlePlayer1Rotation()
    {
        if (Keyboard.current == null) return;
        float horizontalInput = 0f;
        if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) horizontalInput += 1f;
        if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) horizontalInput -= 1f;

        if (Mathf.Abs(horizontalInput) > 0.01f)
        {
            currentRootRotationY += horizontalInput * rotationSpeed * Time.deltaTime;
            
            // Server updates the NetworkVariable directly to update all clients
            if (IsServer) netRootRotationY.Value = currentRootRotationY;
        }
    }

    private void HandlePlayer2Inputs()
    {
        if (Keyboard.current == null) return;

        float verticalInput = 0f;
        if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) verticalInput += 1f;
        if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) verticalInput -= 1f;

        if (Mathf.Abs(verticalInput) > 0.01f)
        {
            currentLeanAngle += verticalInput * leanSpeed * Time.deltaTime;
            currentLeanAngle = Mathf.Clamp(currentLeanAngle, -maxLeanAngle, maxLeanAngle);
            
            // Send local client calculations up to the server host via an RPC call
            if (IsSpawned) SubmitLeanServerRpc(currentLeanAngle);
        }

        if (IsSpawned)
        {
            // Transmit press down and release up state transitions across the network layer
            if (Keyboard.current.qKey.wasPressedThisFrame) SubmitLegLiftServerRpc(true, true);
            if (Keyboard.current.qKey.wasReleasedThisFrame) SubmitLegLiftServerRpc(true, false);

            if (Keyboard.current.eKey.wasPressedThisFrame) SubmitLegLiftServerRpc(false, true);
            if (Keyboard.current.eKey.wasReleasedThisFrame) SubmitLegLiftServerRpc(false, false);
        }
    }

    // RPC endpoints that authorize the server to update the synchronized network variables
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitLeanServerRpc(float leanValue) => netCurrentLean.Value = leanValue;

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitLegLiftServerRpc(bool isLeftLeg, bool isRaised)
    {
        if (isLeftLeg) netLeftLegRaised.Value = isRaised;
        else netRightLegRaised.Value = isRaised;
    }
    #endregion
}