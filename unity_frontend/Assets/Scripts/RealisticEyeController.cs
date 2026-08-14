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

    public float SmoothTorsion => _smoothTorsion;
    public float SmoothVertical => _smoothVertical;
    public float SmoothHorizontal => _smoothHorizontal;

    // ********** Head velocity tracking (used for VOR compensation) *******
    private Vector3 lastHeadRotation;
    private Vector3 headVelocity;

    // ********** Serial-driven state ***************************************
    // Set by ArduinoSerialReader.ApplyDirectEyeAngles() every ~10ms
    // and applied synchronously within that same call.
    private float _directTorsion = 0f;
    private float _directVertical = 0f;
    private float _directHorizontal = 0f;
    private string _activeBppvSide = "right";

    // *********** Smoothing *****************************************************
    // Lerp factor for smoothing serial input to avoid jitter from timing jitter
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
    }

    // -------------------------------------------------------------------------
    // Serial-driven eye control — mirrors physical motor angles directly
    // -------------------------------------------------------------------------

    // *************************************************************************
    // Called by ArduinoSerialReader.Update() every frame with the latest eye
    // angles received from the Arduino. Computes smoothing and applies the
    // resulting rotation synchronously within this call, so SmoothTorsion /
    // SmoothVertical / SmoothHorizontal are already current the instant this
    // method returns, no dependency on Unity's Update() execution order
    // between components.
    // *************************************************************************
    public void ApplyDirectEyeAngles(float torsion, float vertical, float horizontal, string activeBppvSide)
    {
        _directTorsion = torsion;
        _directVertical = vertical;
        _directHorizontal = horizontal;
        _activeBppvSide = activeBppvSide;

        _smoothTorsion = Mathf.Lerp(_directTorsion, _smoothTorsion, serialSmoothing);
        _smoothVertical = Mathf.Lerp(_directVertical, _smoothVertical, serialSmoothing);
        _smoothHorizontal = Mathf.Lerp(_directHorizontal, _smoothHorizontal, serialSmoothing);

        ApplySmoothedRotation();
    }

    private void ApplySmoothedRotation()
    {
        Vector3 vor = headTransform != null ? -headVelocity * vorGain : Vector3.zero;
        Vector3 rightOffset = vor + new Vector3(_smoothHorizontal, _smoothVertical, -_smoothTorsion);
        Vector3 leftOffset  = vor + new Vector3(_smoothHorizontal, _smoothVertical,  _smoothTorsion);
        rightEye.localRotation = Quaternion.Euler(rightOffset);
        leftEye.localRotation  = Quaternion.Euler(leftOffset);
    }

    // ------------------------------------------------------------------------
    // Head velocity tracking, feeds the VOR term above
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
}