using System;
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;

public class ArduinoSerialReader : MonoBehaviour
{
    // ********* Serial settings *************************************
    [Header("Serial Settings")]
    public string portName = "COM3";
    public int baudRate = 115200;

    // ********* References ******************************************
    [Header("References")]
    public Transform headTransform;
    public RealisticEyeController eyeController;
    public InstructionManager instructionManager;

    // ********* Head motion smoothing *************************************
    [Header("Head Motion")]
    public float smoothSpeed = 5f;
    public float headRotationScale = 1.0f;

    // ********* Dix-Hallpike thresholds ***********************************
    [Header("Dix-Hallpike Thresholds")]
    public float yawThreshold = 45f;
    public float pitchThreshold = 20f;

    // ********* Nystagmus timing ******************************************
    [Header("Nystagmus Timing")]
    public float holdBeforeTrigger = 1.0f;

    [Header("BPPV Side — set from patient data")]
    public string activeBppvSide = "right"; // "right" or "left"
    public string activeBppvType = "posterior"; // "posterior", "horizontal", "anterior"

    // ********* Debug *******************************************************
    [Header("Debug")]
    public bool showDebugLog = false;

    // ********* Public state ************************************************
    public int CurrentPhase { get; private set; } = 0;
    public bool IsReceivingData { get; private set; } = false;
    public Vector3 CurrentHeadAngles { get; private set; } = Vector3.zero;

    // ********* Internal serial *********************************************
    private SerialPort _serial;
    private readonly object _serialLock = new object(); // protects _serial access across threads

    private ConcurrentQueue<CombinedFrame> _frameQueue = new ConcurrentQueue<CombinedFrame>();
    private Thread _readThread;
    private volatile bool _running = false; // read by multiple threads

    private float _lastReceiveTime = 0f;
    private const float WATCHDOG_TIMEOUT = 3.0f;

    // ********* IMU state ***************************************************
    private float _curYaw = 0f;
    private float _curPitch = 0f;
    private float _curRoll = 0f;
    private bool _hasFreshReading = false;

    private Quaternion _targetRotation = Quaternion.identity;

    // ********* Step 3 state **********************************************
    private bool _nystagmusTriggered = false;
    private float _nystagmusTimer = 0f;
    private bool _triggerSent = false;
    private float _triggerSentTime = 0f;
    private const float MIN_NYSTAGMUS_TIME = 5f;
    private float _lastYaw;
    private float _lastPitch;
    private float _confirmedYaw = 0f;

    // ********* Frame struct **********************************************
    private struct CombinedFrame
    {
        public float yaw, pitch, roll;
        public float torsion, vertical, horizontal;
        public int phase;
    }

    // ********* UNITY LIFECYCLE *******************************************
    void Start()
    {
        OpenPort();
    }

    void Update()
    {
        // Check if port is open under lock
        bool portOpen;
        lock (_serialLock) { portOpen = _serial != null && _serial.IsOpen; }
        if (!portOpen) return;

        IsReceivingData = (Time.time - _lastReceiveTime) < WATCHDOG_TIMEOUT;

        // Drain queue - keep only the latest frame, discard stale ones
        // This prevents Unity from processing hundreds of backed-up frames
        CombinedFrame latest = default;
        bool hasNew = false;
        while (_frameQueue.TryDequeue(out CombinedFrame frame))
        {
            latest = frame;
            hasNew = true;
        }

        if (hasNew)
        {
            _lastReceiveTime = Time.time;
            CurrentPhase = latest.phase;
            _hasFreshReading = true;

            _curYaw = latest.yaw;
            _curPitch = latest.pitch;
            _curRoll = latest.roll;

            _targetRotation = Quaternion.Euler(
                -latest.pitch * headRotationScale,
                -latest.yaw * headRotationScale,
               -latest.roll * headRotationScale
            );
            CurrentHeadAngles = new Vector3(_curPitch, _curYaw, _curRoll);

            if (eyeController != null)
                eyeController.ApplyDirectEyeAngles(
                    latest.torsion,
                    latest.vertical,
                    latest.horizontal
                );

            if (showDebugLog)
                Debug.Log($"[Serial] Y:{latest.yaw:F1} P:{latest.pitch:F1} R:{latest.roll:F1} " +
                          $"| T:{latest.torsion:F1} V:{latest.vertical:F1} H:{latest.horizontal:F1} " +
                          $"P:{latest.phase}");
        }

        if (_hasFreshReading)
            CheckStepConditions(-_curYaw, -_curPitch, -_curRoll);

        if (headTransform != null)
        {
            headTransform.rotation = Quaternion.Slerp(
                headTransform.rotation,
                _targetRotation,
                Time.deltaTime * smoothSpeed
            );
        }
    }

    void OnDisable() { ClosePort(); }
    void OnDestroy() { ClosePort(); }
    void OnApplicationQuit() { ClosePort(); }

    // -------------------------------------------------------------------------
    // STEP DETECTION
    // -------------------------------------------------------------------------
    private void CheckStepConditions(float yaw, float pitch, float roll)
    {
        if (instructionManager == null) return;

        int step = instructionManager.currentStepIndex;
        if (step < 0) return;

        _lastYaw = yaw;
        _lastPitch = pitch;

        if (SimulationUI.Instance != null &&
            !SimulationUI.Instance.patientModel.activeSelf)
            return;

        if (instructionManager.tts != null && instructionManager.tts.IsSpeaking)
            return;

        switch (step)
        {
            case 0:
                if (Mathf.Abs(yaw) < 10f && Mathf.Abs(pitch) < 10f)
                {
                    Debug.Log($"✔ Step 0: Neutral confirmed (Y:{yaw:F1}° P:{pitch:F1}°)");
                    instructionManager.CompleteStepFromArduino();
                }
                break;

            case 1:
                if (IsHeadRotated45())
                {
                    _confirmedYaw = _lastYaw; // lock yaw at this moment
                    Debug.Log($"✔ Step 1: Head rotated 45° {activeBppvSide} (Y:{_lastYaw:F1}°)");
                    instructionManager.CompleteStepFromArduino();
                }
                else if (Mathf.Abs(_lastYaw) > 10f)
                    Debug.Log($"[Step 1] Progress: yaw={_lastYaw:F1}° / {(activeBppvSide == "right" ? "" : "-")}{yawThreshold}°");
                break;

            case 2:
                if (IsInDixHallpikePosition())
                {
                    Debug.Log($"✔ Step 2: Supine (P:{pitch:F1}° Y:{_confirmedYaw:F1}°)");
                    instructionManager.CompleteStepFromArduino();
                }
                else if (pitch > 5f)
                    Debug.Log($"[Step 2] Progress: pitch={pitch:F1}° / {pitchThreshold}°, yaw={_confirmedYaw:F1}°");
                break;

            case 3:
                if (IsInDixHallpikePosition())
                {
                    if (!_nystagmusTriggered)
                    {
                        _nystagmusTimer += Time.deltaTime;
                        Debug.Log($"[Step 3] Holding... {_nystagmusTimer:F1}s / {holdBeforeTrigger}s");

                        if (_nystagmusTimer >= holdBeforeTrigger)
                        {
                            _nystagmusTriggered = true;
                            _nystagmusTimer = 0f;
                            _triggerSent = true;
                            _triggerSentTime = Time.time;
                            Debug.Log("✔ Latency complete → sending TRIGGER");
                            string command = activeBppvSide == "left" ? "L" : "R";
                            Debug.Log($"[Serial] Sending BPPV command: {command} (type: {activeBppvType})");
                            SendCommand(command);
                        }
                    }
                    else
                    {
                        Debug.Log($"[Step 3] Nystagmus active... Phase:{CurrentPhase}");
                        bool minTimePassed = (Time.time - _triggerSentTime) >= MIN_NYSTAGMUS_TIME;
                        bool motorsAtReversal = CurrentPhase == 5;

                        if (_triggerSent && minTimePassed && motorsAtReversal)
                        {
                            Debug.Log("✔ Step 3 complete: Nystagmus finished");
                            _nystagmusTriggered = false;
                            _nystagmusTimer = 0f;
                            _triggerSent = false;
                            instructionManager.CompleteStepFromArduino();
                        }
                    }
                }
                else
                {
                    Debug.Log($"[Step 3] Nystagmus active... Phase:{CurrentPhase}");
                    if (CurrentPhase == 5 && _nystagmusTriggered)
                    {
                        Debug.Log("✔ Step 3 complete: Nystagmus finished");
                        _nystagmusTriggered = false;
                        _nystagmusTimer = 0f;
                        instructionManager.CompleteStepFromArduino();
                    }
                }
                break;

            case 4:
                if (Mathf.Abs(pitch) < 15f)
                {
                    if (CurrentPhase == 0)
                    {
                        Debug.Log($"✔ Step 4 complete: Upright and motors neutral (P:{pitch:F1}°)");
                        instructionManager.CompleteStepFromArduino();
                    }
                    else
                        Debug.Log($"[Step 4] Upright but waiting for motor reversal. Phase:{CurrentPhase}");
                }
                else if (Mathf.Abs(pitch) < 30f)
                    Debug.Log($"[Step 4] Returning... pitch={pitch:F1}°");
                break;
        }
    }

    private bool IsHeadRotated45()
    {
        if (activeBppvSide == "right")
            return _lastYaw >= yawThreshold - 2f && _lastYaw < yawThreshold + 2f;
        else
            return _lastYaw <= -yawThreshold + 2f && _lastYaw > -yawThreshold - 2f;
    }
    private bool IsInDixHallpikePosition()
    {        
        if (activeBppvSide == "right")
            return _lastPitch >= pitchThreshold && _confirmedYaw >= yawThreshold - 2f;
        else
            return _lastPitch >= pitchThreshold && _confirmedYaw <= -yawThreshold + 2f;
    }
    // ------------------------------------------------------------------
    // SERIAL PORT
    // ------------------------------------------------------------------
    private void OpenPort()
    {
        lock (_serialLock)
        {
            try
            {
                // Fully dispose any existing port before creating new one
                if (_serial != null)
                {
                    if (_serial.IsOpen) _serial.Close();
                    _serial.Dispose();
                    _serial = null;
                }

                _serial = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 500, // 500ms - never blocks the thread forever
                    WriteTimeout = 500,
                    DtrEnable = false // prevents Arduino reset on connect
                };
                _serial.Open();

                // Flush stale bytes sitting in the OS buffer from a previous session
                Thread.Sleep(150);
                _serial.DiscardInBuffer();
                _serial.DiscardOutBuffer();

                Debug.Log($"✓ Serial connected to {portName} at {baudRate} baud");
            }
            catch (Exception e)
            {
                Debug.LogError($"❌ Could not open {portName}: {e.Message}");
                _serial = null;
                return;
            }
        }

        // Start background read thread
        _running = true;
        _readThread = new Thread(ReadLoop) { IsBackground = true };
        _readThread.Start();
    }

    private void ReadLoop()
{
    while (_running)
    {
        try
        {
            bool portOpen;
            lock (_serialLock) { portOpen = _serial != null && _serial.IsOpen; }

            if (!portOpen)
            {
                Thread.Sleep(2000);
                TryReconnect();
                continue;
            }

            // ReadLine outside the lock - blocking call must never hold the lock
            string line = _serial.ReadLine().Trim();

            if (string.IsNullOrEmpty(line)) continue;

            if (line.Contains("Calibration") || line.Contains("MPU") ||
                line.Contains("DMP") || line.Contains("Offsets") ||
                line.Contains("Waiting") || line.Contains("Ready") ||
                line.Contains("CMD:") || line.Contains("PHASE:") ||
                line.Contains("BPPV") || line.Contains("Motors"))
            {
                Debug.Log($"[Arduino] {line}");
                continue;
            }

            if (!line.StartsWith("H:")) continue;

            if (TryParseLine(line, out CombinedFrame frame))
            {
                _frameQueue.Enqueue(frame);
                while (_frameQueue.Count > 50)
                    _frameQueue.TryDequeue(out _);
            }
        }
        catch (TimeoutException) { }
        catch (ThreadAbortException) { break; }
        catch (Exception e)
        {
            if (!_running) break;

            bool portOpen;
            lock (_serialLock) { portOpen = _serial != null && _serial.IsOpen; }

            if (!portOpen)
            {
                Debug.LogWarning($"[Serial] Port closed: {e.Message}");
                Thread.Sleep(2000);
                TryReconnect();
            }
            else if (showDebugLog)
                Debug.LogWarning($"[Serial] Read warning: {e.Message}");
        }
    }
    Debug.Log("ReadLoop exited");
}

    private void TryReconnect()
    {
        Debug.Log("[Serial] Attempting reconnect...");
        lock (_serialLock)
        {
            try
            {
                // Fully dispose - reusing a closed SerialPort object is unreliable on Windows
                if (_serial != null)
                {
                    try { if (_serial.IsOpen) _serial.Close(); } catch { }
                    try { _serial.Dispose(); } catch { }
                    _serial = null;
                }

                Thread.Sleep(500);

                _serial = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = false
                };
                _serial.Open();

                Thread.Sleep(150);
                _serial.DiscardInBuffer();
                _serial.DiscardOutBuffer();

                Debug.Log("[Serial] Reconnected successfully.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Serial] Reconnect failed: {e.Message} - will retry in 2s");
                if (_serial != null)
                {
                    try { _serial.Dispose(); } catch { }
                    _serial = null;
                }
            }
        }
    }

    private void ClosePort()
    {
        _running = false;

        if (_readThread != null && _readThread.IsAlive)
        {
            _readThread.Join(1000);
            _readThread = null;
        }

        lock (_serialLock)
        {
            if (_serial != null)
            {
                try { if (_serial.IsOpen) _serial.Close(); } catch { }
                try { _serial.Dispose(); } catch { }
                _serial = null;
                Debug.Log("[Serial] Port closed.");
            }
        }
    }

    // ------------------------------------------------------------------
    // PARSE - H:yaw,pitch,roll|T:torsion,V:vertical,H:horizontal,P:phase
    // ------------------------------------------------------------------
    private bool TryParseLine(string line, out CombinedFrame frame)
    {
        frame = default;
        try
        {
            string[] sections = line.Split('|');
            if (sections.Length < 2) return false;

            // Head section: H:yaw,pitch,roll
            string[] h = sections[0].Substring(2).Split(',');
            if (h.Length < 3) return false;
            frame.yaw = float.Parse(h[0]);
            frame.pitch = float.Parse(h[1]);
            frame.roll = float.Parse(h[2]);

            // Eye section: T:val,V:val,H:val,P:val,S:R
            // Split by comma then extract value after colon for each field
            // S field is ignored — side is managed by patient JSON not Arduino broadcast
            string[] e = sections[1].Split(',');
            if (e.Length < 4) return false;

            frame.torsion = float.Parse(e[0].Split(':')[1]);
            frame.vertical = float.Parse(e[1].Split(':')[1]);
            frame.horizontal = float.Parse(e[2].Split(':')[1]);
            frame.phase = int.Parse(e[3].Split(':')[1]);
            // e[4] is S:R or S:L - deliberately ignored here

            return true;
        }
        catch { return false; }
}

    // ------------------------------------------------------------------
    // PUBLIC API
    // ------------------------------------------------------------------
    public void SendCommand(string cmd)
    {
        lock (_serialLock)
        {
            if (_serial != null && _serial.IsOpen)
            {
                _serial.WriteLine(cmd);
                Debug.Log($"[Serial] Sent: {cmd}");
            }
            else
                Debug.LogWarning("[Serial] Cannot send — port not open.");
        }
    }

    public void TriggerBPPV() { SendCommand("TRIGGER"); }
    public void TriggerNeutral() { SendCommand("NEUTRAL"); }

    public void ResetBPPVState()
    {
        _hasFreshReading = false;
        _nystagmusTriggered = false;
        _nystagmusTimer = 0f;
        _triggerSent = false;
        _triggerSentTime = 0f;
    }

    public void SetPatientBPPVInfo(string side, string type)
    {
        activeBppvSide = side.ToLower().Trim();
        activeBppvType = type.ToLower().Trim();
        Debug.Log($"[Serial] BPPV set - side: {activeBppvSide}, type: {activeBppvType}");
    }
}