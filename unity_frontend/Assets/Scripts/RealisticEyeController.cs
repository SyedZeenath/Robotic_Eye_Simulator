using System;
using UnityEngine;

public class RealisticEyeController : MonoBehaviour
{
    [Header("Head")]
    public Transform headTransform;

    [Header("Eyes")]
    public Transform leftEye;
    public Transform rightEye;

    [Header("VOR Settings")]
    public float vorGain = 0.9f;

    // ********** TO DO: ROS2 based variable/method cleanup needed *******************
    [Header("BPPV Internal Simulation (ROS2 mode only)")]
    public float maxSlowPhaseVelocity = 25f;
    public float riseTime = 1.0f;
    public float decayTime = 8.0f;
    public float fastPhaseThreshold = 12f;
    public float fastPhaseSpeed = 250f;

    // ********** MODE 1: Internal simulation state *******************************
    private Vector3 lastHeadRotation;
    private Vector3 headVelocity;
    private float eyeAngle;
    private float stimulus;
    private float timeSinceTrigger;
    private bool fastPhaseActive;
    private float fastPhaseTimer;
    private int activeCanal;

    // ********** MODE 2: Direct Serial state *******************************
    // These are set by ArduinoSerialReader.ApplyDirectEyeAngles() every 10ms
    // and applied in Update() via VOR + direct offset combination
    private float _directTorsion = 0f;
    private float _directVertical = 0f;
    private float _directHorizontal = 0f;
    private string _activeBppvSide = "right"; // default to right
    private bool _serialMode = false; // true when Serial data is arriving

    // *********** Smoothing *****************************************************
    // Lerp factor for smoothing Serial input to avoid jitter from timing jitter
    // 0.0 = no smoothing (raw), 1.0 = completely frozen
    // 0.15 gives one-frame smoothing at 100Hz Arduino / 60Hz Unity
    [Header("Serial Mode Smoothing")]
    [Range(0f, 0.5f)]
    public float serialSmoothing = 0.15f;

    // *********** Current smoothed angles *************************************************
    private float _smoothTorsion = 0f;
    private float _smoothVertical = 0f;
    private float _smoothHorizontal = 0f;

    // *********** Current phase label for UI display *****************************************
    public int CurrentPhase { get; private set; } = 0;

    void Start()
    {
        lastHeadRotation = headTransform != null
            ? headTransform.localEulerAngles
            : Vector3.zero;
    }

    void Update()
    {
        if (headTransform != null)
            CalculateHeadVelocity();

        if (_serialMode)
            UpdateSerialMode();
        else
            UpdateInternalSimulation(Time.deltaTime);
    }

    // -------------------------------------------------------------------------
    // MODE 2: SERIAL — mirror physical motor angles directly
    // -------------------------------------------------------------------------

    // *************************************************************************
    // Called by ArduinoSerialReader.Update() every frame with latest eye angles
    // from Arduino. Stores values for use in UpdateSerialMode().
    // *************************************************************************
    public void ApplyDirectEyeAngles(float torsion, float vertical, float horizontal, string activeBppvSide)
    {
        _directTorsion = torsion;
        _directVertical = vertical;
        _directHorizontal = horizontal;
        _activeBppvSide = activeBppvSide;
        _serialMode = true;
    }

    // *************************************************************************
    // Apply Serial eye angles to Unity eye transforms each frame
    // Combines VOR (head stabilisation reflex) with the physical eye offset
    // so the Unity eye behaves like a real eye (compensates for head movement
    // while also showing the BPPV nystagmus from the physical robot)
    // *************************************************************************
    private void UpdateSerialMode()
    {
        // Smooth incoming Serial values to absorb timing jitter
        // Lerp from current smooth value toward new target
        // serialSmoothing = 0.15 means 85% of new value applied each frame
        _smoothTorsion = Mathf.Lerp(_directTorsion, _smoothTorsion, serialSmoothing);
        _smoothVertical = Mathf.Lerp(_directVertical, _smoothVertical, serialSmoothing);
        _smoothHorizontal = Mathf.Lerp(_directHorizontal, _smoothHorizontal, serialSmoothing);

        // VOR: compensatory eye movement opposing head motion
        // Keeps gaze stable when head moves, same as in internal simulation mode
        Vector3 vor = headTransform != null
            ? -headVelocity * vorGain
            : Vector3.zero;

        Vector3 rightOffset = vor + new Vector3(_smoothHorizontal, _smoothVertical, -_smoothTorsion);
        Vector3 leftOffset  = vor + new Vector3(_smoothHorizontal, _smoothVertical,  _smoothTorsion);

        rightEye.localRotation = Quaternion.Euler(rightOffset);
        leftEye.localRotation  = Quaternion.Euler(leftOffset);
    }

    // ------------------------------------------------------------------------
    // MODE 1: INTERNAL SIMULATION (original ROS2 mode, unchanged)
    // ------------------------------------------------------------------------

    void CalculateHeadVelocity()
    {
        Vector3 current = headTransform.localEulerAngles;
        Vector3 delta = new Vector3(
            Mathf.DeltaAngle(lastHeadRotation.x, current.x),
            Mathf.DeltaAngle(lastHeadRotation.y, current.y),
            Mathf.DeltaAngle(lastHeadRotation.z, current.z)
        );
        headVelocity = delta / Time.deltaTime;
        lastHeadRotation = current;
    }

    void UpdateInternalSimulation(float dt)
    {
        timeSinceTrigger += dt;

        Vector3 vor = headTransform != null
            ? -headVelocity * vorGain
            : Vector3.zero;

        float envelope = ComputeEnvelope(timeSinceTrigger);
        float slowPhaseVelocity = maxSlowPhaseVelocity * stimulus * envelope;

        eyeAngle += slowPhaseVelocity * dt;

        if (!fastPhaseActive && Mathf.Abs(eyeAngle) > fastPhaseThreshold)
        {
            fastPhaseActive = true;
            fastPhaseTimer = 0f;
        }

        if (fastPhaseActive)
        {
            fastPhaseTimer += dt;
            float direction = Mathf.Sign(eyeAngle);
            eyeAngle -= direction * fastPhaseSpeed * dt;

            if (fastPhaseTimer > 0.05f)
            {
                fastPhaseActive = false;
                eyeAngle = 0f;
            }
        }

        Vector3 nystagmusOffset = GetCanalPattern(activeCanal, eyeAngle);
        Vector3 final = vor + nystagmusOffset;

        if (_activeBppvSide == "left")
        {
            leftEye.localRotation = Quaternion.Euler(final);
        }
        else
        {
            rightEye.localRotation = Quaternion.Euler(final);
        }
    }

    float ComputeEnvelope(float t)
    {
        if (t < 2f) return 0f;
        float t2 = t - 2f;
        float rise = 1f - Mathf.Exp(-t2 / riseTime);
        float decay = Mathf.Exp(-t2 / decayTime);
        return rise * decay;
    }

    Vector3 GetCanalPattern(int canal, float angle)
    {
        switch (canal)
        {
            case 1: return new Vector3(angle, 0f, angle * 0.6f); // Posterior
            case 2: return new Vector3(0f, angle, 0f); // Horizontal
            case 3: return new Vector3(-angle, 0f, angle * 0.4f); // Anterior
            default: return Vector3.zero;
        }
    }

    // TO DO: Update this method later
    // Original ROS2 trigger method - kept intact for compatibility
    // Switches to internal simulation mode
    public void ApplySimulationData(Vector3 headRotation, int canal, float amp, float freq)
    {
        if (headTransform != null)
            headTransform.localRotation = Quaternion.Euler(headRotation);

        activeCanal = canal;
        stimulus = Mathf.Clamp01(amp);
        timeSinceTrigger = 0f;
        eyeAngle = 0f;
        fastPhaseActive = false;

        // Switch to internal simulation mode when triggered via ROS2
        _serialMode = false;

        Debug.Log($"[RealisticEyeController] ROS2 BPPV triggered: " +
                  $"Canal {canal}, Amplitude {amp}");
    }
}