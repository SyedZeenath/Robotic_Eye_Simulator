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
    public float noNystagmusHoldTime = 5.0f;  // hold time on unaffected side

    // ********* BPPV info (set from patient data) *****************************
    [Header("BPPV Side")]
    public string activeBppvSide = "right";
    public string activeBppvType = "posterior";

    // ********* Lie-back speed thresholds ****************************************
    [Header("Lie-Back Speed")]
    public float fastLieBackThreshold = 15f;
    public float acceptableLieBackThreshold = 8f;

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

    // ********* Pitch velocity ***************************************************
    private float _prevPitch = 0f;
    private float _prevPitchTime = 0f;
    private float _peakPitchVelocity = 0f;
    private float[] _pitchVelocityBuffer = new float[5];
    private int _pvBufferIndex = 0;
    private float _smoothedPitchVelocity = 0f;

    // ********* Step state ***************************************************
    private float _lastYaw = 0f;
    private float _lastPitch = 0f;
    private float _confirmedYaw = 0f;

    private bool _nystagmusTriggered = false;
    private float _nystagmusTimer = 0f;
    private bool _triggerSent = false;
    private float _triggerSentTime = 0f;
    private float _noNystagmusHoldTimer = 0f;
    private const float MIN_NYSTAGMUS_TIME = 5f;

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
        // One quick lock just to read the bool - never hold across calls
        bool portOpen;
        lock (_serialLock) { portOpen = _serial != null && _serial.IsOpen; }
        if (!portOpen) return;

        IsReceivingData = (Time.time - _lastReceiveTime) < WATCHDOG_TIMEOUT;

        // Drain queue on main thread - ConcurrentQueue needs no lock
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
                    latest.horizontal,
                    activeBppvSide
                );

            if (showDebugLog)
                Debug.Log($"[Serial] Y:{latest.yaw:F1} P:{latest.pitch:F1} R:{latest.roll:F1} " +
                          $"T:{latest.torsion:F1} V:{latest.vertical:F1} H:{latest.horizontal:F1} " +
                          $"Ph:{latest.phase}");
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

        // Smoothed pitch velocity - computed every frame on main thread
        float dt = Mathf.Max(Time.time - _prevPitchTime, 0.001f);
        float rawVelocity = (pitch - _prevPitch) / dt;
        _prevPitch = pitch;
        _prevPitchTime = Time.time;

        _pitchVelocityBuffer[_pvBufferIndex % 5] = rawVelocity;
        _pvBufferIndex++;
        _smoothedPitchVelocity = 0f;
        foreach (float v in _pitchVelocityBuffer)
            _smoothedPitchVelocity += v;
        _smoothedPitchVelocity /= 5f;

        switch (step)
        {
            // Step 0: Confirm neutral position
            case 0:
                if (Mathf.Abs(yaw) < 10f && Mathf.Abs(pitch) < 10f)
                {
                    Debug.Log($"✔ Step 0: Neutral confirmed (Y:{yaw:F1}° P:{pitch:F1}°)");
                    instructionManager.CompleteStep();
                }
                break;

            // Step 1: Turn head RIGHT 45 degrees
            case 1:
                if (yaw >= yawThreshold - 2f && yaw <= yawThreshold + 15f)
                {
                    _confirmedYaw = yaw;
                    Debug.Log($"✔ Step 1: Head turned right (Y:{yaw:F1}°)");
                    instructionManager.CompleteStep();
                }
                else if (yaw > 10f)
                    Debug.Log($"[Step 1] Progress: yaw={yaw:F1}° / {yawThreshold}°");
                break;

            // Step 2: Lie back with head turned right
            case 2:
                if (_smoothedPitchVelocity > 0f)
                    _peakPitchVelocity = Mathf.Max(_peakPitchVelocity, _smoothedPitchVelocity);

                if (pitch >= pitchThreshold && _confirmedYaw >= yawThreshold - 2f)
                {
                    EvaluateLieBackSpeed(_peakPitchVelocity, step);
                    _peakPitchVelocity = 0f;
                    Debug.Log($"✔ Step 2: Right Dix-Hallpike (P:{pitch:F1}° Y:{_confirmedYaw:F1}°)");
                    instructionManager.CompleteStep();
                }
                else if (pitch > 5f)
                    Debug.Log($"[Step 2] Progress: pitch={pitch:F1}° / {pitchThreshold}° yaw={_confirmedYaw:F1}°");
                break;

            // Step 3: Hold right position and observe
            // If right BPPV: nystagmus fires and completes naturally
            // If left BPPV: no nystagmus, hold 5 seconds then advance
            case 3:
                if (pitch >= pitchThreshold && _confirmedYaw >= yawThreshold - 2f)
                {
                    if (activeBppvSide == "right")
                        HandleNystagmusSide();
                    else
                        HandleUnaffectedSideHold();
                }
                break;

            // Step 4: Return to neutral after right side
            case 4:
                if (Mathf.Abs(pitch) < 15f && Mathf.Abs(yaw) < 20f)
                {
                    bool settled = activeBppvSide == "right" ? CurrentPhase == 0 : true;
                    if (settled)
                    {
                        Debug.Log($"✔ Step 4: Neutral after right (P:{pitch:F1}°)");
                        ResetHoldState();
                        instructionManager.CompleteStep();
                    }
                    else
                        Debug.Log($"[Step 4] Waiting for motor reversal. Phase:{CurrentPhase}");
                }
                else if (Mathf.Abs(pitch) < 30f)
                    Debug.Log($"[Step 4] Returning... pitch={pitch:F1}°");
                break;

            // Step 5: Turn head LEFT 45 degrees
            case 5:
                if (yaw <= -yawThreshold + 2f && yaw >= -yawThreshold - 15f)
                {
                    _confirmedYaw = yaw;
                    Debug.Log($"✔ Step 5: Head turned left (Y:{yaw:F1}°)");
                    instructionManager.CompleteStep();
                }
                else if (yaw < -10f)
                    Debug.Log($"[Step 5] yaw={yaw:F1}° / -{yawThreshold}°");
                break;

            // Step 6: Lie back with head turned left
            case 6:
                if (_smoothedPitchVelocity > 0f)
                    _peakPitchVelocity = Mathf.Max(_peakPitchVelocity, _smoothedPitchVelocity);

                if (pitch >= pitchThreshold && _confirmedYaw <= -yawThreshold + 2f)
                {
                    EvaluateLieBackSpeed(_peakPitchVelocity, step);
                    _peakPitchVelocity = 0f;
                    Debug.Log($"✔ Step 6: Left Dix-Hallpike (P:{pitch:F1}° Y:{_confirmedYaw:F1}°)");
                    instructionManager.CompleteStep();
                }
                else if (pitch > 5f)
                    Debug.Log($"[Step 6] pitch={pitch:F1}° spd={_smoothedPitchVelocity:F1}°/s peak={_peakPitchVelocity:F1}°/s");
                break;

            // Step 7: Hold left position and observe
            // If left BPPV: nystagmus fires and completes naturally
            // If right BPPV: no nystagmus, hold 5 seconds then advance
            case 7:
                if (pitch >= pitchThreshold && _confirmedYaw <= -yawThreshold + 2f)
                {
                    if (activeBppvSide == "left")
                        HandleNystagmusSide();
                    else
                        HandleUnaffectedSideHold();
                }
                break;

            // Step 8: Return to neutral after left side
            case 8:
                if (Mathf.Abs(pitch) < 15f && Mathf.Abs(yaw) < 20f)
                {
                    bool settled = activeBppvSide == "left" ? CurrentPhase == 0 : true;
                    if (settled)
                    {
                        Debug.Log($"✔ Step 8: Neutral after left (P:{pitch:F1}°)");
                        ResetHoldState();
                        instructionManager.CompleteStep();
                    }
                    else
                        Debug.Log($"[Step 8] Waiting for motor reversal. Phase:{CurrentPhase}");
                }
                else if (Mathf.Abs(pitch) < 30f)
                    Debug.Log($"[Step 8] Returning... pitch={pitch:F1}°");
                break;

            // Step 9: Diagnosis fired automatically when Step 8 completes
            case 9:
                break;
        }
    }

    // ********* NYSTAGMUS HELPERS **************************************************
    private void HandleNystagmusSide()
    {
        int step = instructionManager.currentStepIndex;
        if (!_nystagmusTriggered)
        {
            _nystagmusTimer += Time.deltaTime;
            if (_nystagmusTimer >= holdBeforeTrigger)
            {
                _nystagmusTriggered = true;
                _nystagmusTimer = 0f;
                _triggerSent = true;
                _triggerSentTime = Time.time;
                string command = activeBppvSide == "left" ? "L" : "R";
                Debug.Log($"✔ Step {step}: Sending {command}");
                SendCommand(command);
            }
        }
        else
        {
            bool minTimePassed = (Time.time - _triggerSentTime) >= MIN_NYSTAGMUS_TIME;
            bool atReversal = CurrentPhase == 5;
            Debug.Log($"[Step {step}] Phase:{CurrentPhase} MinTime:{minTimePassed}");
            if (_triggerSent && minTimePassed && atReversal)
            {
                Debug.Log($"✔ Step {step} complete: Nystagmus finished");
                _nystagmusTriggered = false;
                _nystagmusTimer = 0f;
                _triggerSent = false;
                instructionManager.CompleteStep();
            }
        }
    }

    private void HandleUnaffectedSideHold()
    {
        int step = instructionManager.currentStepIndex;
        _noNystagmusHoldTimer += Time.deltaTime;
        Debug.Log($"[Step {step}] Unaffected hold {_noNystagmusHoldTimer:F1}s / {noNystagmusHoldTime}s");
        if (_noNystagmusHoldTimer >= noNystagmusHoldTime)
        {
            Debug.Log($"✔ Step {step} complete: No nystagmus on unaffected side");
            _noNystagmusHoldTimer = 0f;
            instructionManager.CompleteStep();
        }
    }

    private void ResetHoldState()
    {
        _nystagmusTriggered = false;
        _nystagmusTimer = 0f;
        _triggerSent = false;
        _triggerSentTime = 0f;
        _noNystagmusHoldTimer = 0f;
    }

    private void EvaluateLieBackSpeed(float peakSpeed, int step)
    {
        string side = (step == 2) ? "right" : "left";
        if (peakSpeed >= fastLieBackThreshold)
        {
            Debug.Log($"[Step {step}] Lie-back FAST: {peakSpeed:F1}°/s");
        }
        else if (peakSpeed >= acceptableLieBackThreshold)
        {
            Debug.Log($"[Step {step}] Lie-back ACCEPTABLE: {peakSpeed:F1}°/s");
            instructionManager.tts?.SpeakOnDemand(
                $"Tip: try to lower the patient slightly faster on the {side} side next time.");
        }
        else
        {
            Debug.Log($"[Step {step}] Lie-back TOO SLOW: {peakSpeed:F1}°/s");
            instructionManager.tts?.SpeakOnDemand(
                $"The repositioning on the {side} side was too slow. This may reduce the nystagmus response.");
        }
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
                if (_serial != null)
                {
                    try { if (_serial.IsOpen) _serial.Close(); } catch { }
                    try { _serial.Dispose(); } catch { }
                    _serial = null;
                }

                _serial = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = false
                };
                _serial.Open();

                // Flush stale bytes sitting in the OS buffer from a previous session
                Thread.Sleep(150);
                _serial.DiscardInBuffer();
                _serial.DiscardOutBuffer();

                Thread.Sleep(200); // Give Arduino time to reset and stabilise
                Debug.Log($"✓ Serial connected to {portName} at {baudRate}");
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
        Debug.Log("[Serial] ReadLoop started");
        while (_running)
        {
            // Quick lock just to check open state - never hold across ReadLine
            bool portOpen;
            lock (_serialLock) { portOpen = _serial != null && _serial.IsOpen; }

            if (!portOpen)
            {
                Thread.Sleep(2000);
                TryReconnect();
                continue;
            }

            string line = null;

            // ReadLine is completely outside any lock
            // If it blocks for ReadTimeout ms, the lock is FREE the whole time
            try
            {
                line = _serial.ReadLine();
                if (showDebugLog && line != null)
                {
                    Debug.Log($"[Serial RAW] {line.Trim()}");
                }
            }
            catch (TimeoutException)
            {
                // Normal — no data for 500ms, just loop
                continue;
            }
            catch (InvalidOperationException)
            {
                // Port was closed from another thread mid-read
                Thread.Sleep(2000);
                TryReconnect();
                continue;
            }
            catch (Exception e)
            {
                if (!_running) break;
                Debug.LogWarning($"[Serial] Read error: {e.Message}");
                Thread.Sleep(100);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            string trimmed = line.Trim();

            // Pass Arduino startup messages to console and skip
            if (!trimmed.StartsWith("H:"))
            {
                if (showDebugLog) Debug.Log($"[Arduino] {trimmed}");
                continue;
            }

            if (TryParseLine(trimmed, out CombinedFrame frame))
            {
                _frameQueue.Enqueue(frame);
                // Cap queue - ConcurrentQueue is thread-safe, no lock needed
                while (_frameQueue.Count > 50)
                    _frameQueue.TryDequeue(out _);
            }
        }

        Debug.Log("[Serial] ReadLoop exited.");
    }

    private void TryReconnect()
    {
        Debug.Log("[Serial] Attempting reconnect...");

        // Step 1: Grab the old port reference and null _serial atomically
        // This keeps the lock duration to microseconds
        SerialPort old = null;
        lock (_serialLock)
        {
            old = _serial;
            _serial = null;
        }

        // Step 2: Dispose the old port OUTSIDE the lock
        // This can take time on Windows — we must not hold the lock here
        if (old != null)
        {
            try { old.Close(); } catch { }
            try { old.Dispose(); } catch { }
        }

        Thread.Sleep(500);

        // Step 3: Create and open the new port OUTSIDE the lock
        SerialPort next = null;
        try
        {
            next = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = false
            };
            next.Open();
            Thread.Sleep(150);
            next.DiscardInBuffer();
            Thread.Sleep(200); // Give Arduino time to reset and stabilise
            Debug.Log("[Serial] Reconnected.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Serial] Reconnect failed: {e.Message}");
            try { next?.Dispose(); } catch { }
            next = null;
        }

        // Step 4: Assign the new port atomically — lock is only held for assignment
        lock (_serialLock) { _serial = next; }
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
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            string[] sections = line.Split('|');
            if (sections.Length < 2) return false;

            string[] h = sections[0].Substring(2).Split(',');
            if (h.Length < 3) return false;
            frame.yaw = float.Parse(h[0], culture);
            frame.pitch = float.Parse(h[1], culture);
            frame.roll = float.Parse(h[2], culture);

            string[] e = sections[1].Split(',');
            if (e.Length < 4) return false;
            frame.torsion = float.Parse(e[0].Split(':')[1], culture);
            frame.vertical = float.Parse(e[1].Split(':')[1], culture);
            frame.horizontal = float.Parse(e[2].Split(':')[1], culture);
            frame.phase = int.Parse(e[3].Split(':')[1]);

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Parse] Failed: {ex.Message} on line: {line}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // PUBLIC API
    // ------------------------------------------------------------------
    public void SendCommand(string cmd)
    {
        // Lock only for the Write — never during ReadLine
        lock (_serialLock)
        {
            if (_serial == null || !_serial.IsOpen)
            {
                Debug.LogWarning("[Serial] Cannot send — port not open.");
                return;
            }
            try
            {
                _serial.WriteLine(cmd);
                Debug.Log($"[Serial] Sent: {cmd}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Serial] Send failed: {e.Message}");
            }
        }
    }

    public void SetPatientBPPVInfo(string side, string type)
    {
        activeBppvSide = side.ToLower().Trim();
        activeBppvType = type.ToLower().Trim();
        ResetHoldState();
        Debug.Log($"[Serial] BPPV set: side={activeBppvSide} type={activeBppvType}");
    }

    public void ResetBPPVState()
    {
        _hasFreshReading = false;
        _confirmedYaw = 0f;
        ResetHoldState();
    }

    public void TriggerBPPV() { SendCommand("TRIGGER"); }
    public void TriggerNeutral() { SendCommand("NEUTRAL"); }
}