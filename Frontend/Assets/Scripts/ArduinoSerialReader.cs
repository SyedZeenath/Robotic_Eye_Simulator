using System;
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private bool _supineReached = false;
    public float supineThreshold = 80f;
    public float hangBelowSupine = 20f;

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

    // ********* Evaluation report **********************************************
    [Header("Evaluation Report")]
    [Tooltip("Press this key once, after you have finished collecting all trials, " +
             "to print the final aggregated LaTeX blocks for latency and sync error, " +
             "computed fresh from the accumulated CSV files on disk.")]
    public KeyCode finalReportKey = KeyCode.F9;

    // ********* Serial commands **********************************************
    private const string CMD_TRIGGER_RIGHT = "R";
    private const string CMD_TRIGGER_LEFT = "L";
    private const string CMD_TRIGGER = "TRIGGER";
    private const string CMD_NEUTRAL = "NEUTRAL";

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
    private float _curTilt = 0f;
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
    private float _confirmedYaw = 0f;

    private bool _nystagmusTriggered = false;
    private float _nystagmusTimer = 0f;
    private bool _triggerSent = false;
    private float _triggerSentTime = 0f;
    private float _noNystagmusHoldTimer = 0f;
    private const float MIN_NYSTAGMUS_TIME = 5f;

    // ********* Latency / sync evaluation *************************************
    // All computed on Unity's own clock, no cross-device timestamp matching needed.
    // Per-trial values are appended to CSV files on disk (latency_results.csv,
    // sync_results.csv), which survive across separate Play sessions, since
    // typical usage is one patient/procedure per Play session, stop, then Run
    // again for the next one - in-memory fields alone would reset each time.
    private struct RunningStats
    {
        public int n;
        public float mean, m2, max;

        public void Update(float x)
        {
            n++;
            float delta = x - mean;
            mean += delta / n;
            m2 += delta * (x - mean);
            if (n == 1 || x > max) max = x;
        }

        public float Variance => n > 1 ? m2 / (n - 1) : 0f;
        public float StdDev => Mathf.Sqrt(Variance);
    }

    private RunningStats _latencyStats;
    private List<float> _latencySamples = new List<float>();
    private float _lastCommandSentTime = 0f;
    private bool _awaitingLatency = false;

    private RunningStats _syncStats;
    private List<Vector2> _syncSeries = new List<Vector2>();
    private float _trialStartTime = 0f;
    private float _lastSyncSampleTime = 0f;
    private int _prevPhase = 0;

    // ---- One-way transfer latency (Arduino send -> Unity receive) ----
    // Distinct from _latencyStats above: that one is a round trip (Unity
    // sends a command, waits for phase==1 to come back). This is the
    // continuous-stream, one-way "data transfer latency" the System
    // Integration objective actually asks for. See LatencyMonitor.cs.
    private bool _transferLatencyFlushed = false;

    // ********* Frame struct **********************************************
    private struct CombinedFrame
    {
        public float yaw, pitch, roll;
        public float torsion, vertical, horizontal;
        public int phase;
        public float tilt;
        public long arduinoTs; // -1 if the line had no TS: field
    }

    // ********* UNITY LIFECYCLE *******************************************
    void Start()
    {
        LatencyMonitor.Reset(); // fresh transfer-latency stats for this Play session
        OpenPort();
    }

    void Update()
    {
        if (Input.GetKeyDown(finalReportKey))
            PrintFinalAggregateReport();

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
            _curTilt = latest.tilt;

            _targetRotation = Quaternion.Euler(
                latest.pitch * headRotationScale,
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

            // ---- One-way transfer latency capture ----
            // Every line carries Arduino's send-time now (TS: field). Feed it
            // to LatencyMonitor, which works out the clock offset itself from
            // the running minimum gap (see LatencyMonitor.cs for why).
            if (latest.arduinoTs >= 0)
                LatencyMonitor.Record(latest.arduinoTs);

            // ---- Latency capture ----
            // First frame reporting phase 1 after a command was sent marks
            // the moment the system actually responded to the trigger.
            if (_awaitingLatency && latest.phase == 1)
            {
                float latencyMs = (Time.realtimeSinceStartup - _lastCommandSentTime) * 1000f;
                _latencyStats.Update(latencyMs);
                if (_latencySamples.Count < 50) _latencySamples.Add(latencyMs);
                AppendLatencyToFile(latencyMs);
                Debug.Log($"[Latency] {latencyMs:F1} ms");
                _awaitingLatency = false;
            }

            // ---- Trial start detection (phase 0 -> nonzero) ----
            if (_prevPhase == 0 && latest.phase != 0)
            {
                _syncStats = new RunningStats();
                _syncSeries.Clear();
                _trialStartTime = Time.realtimeSinceStartup;
                _lastSyncSampleTime = 0f;
            }

            // ---- Sync error accumulation, while a trial is active ----
            if (latest.phase >= 1 && latest.phase <= 5 && eyeController != null)
            {
                float et = latest.torsion - eyeController.SmoothTorsion;
                float ev = latest.vertical - eyeController.SmoothVertical;
                float eh = latest.horizontal - eyeController.SmoothHorizontal;
                float combined = Mathf.Sqrt(et * et + ev * ev + eh * eh);
                _syncStats.Update(combined);

                float tSinceStart = Time.realtimeSinceStartup - _trialStartTime;
                if (tSinceStart - _lastSyncSampleTime >= 0.5f && _syncSeries.Count < 60)
                {
                    _syncSeries.Add(new Vector2(tSinceStart, combined));
                    _lastSyncSampleTime = tSinceStart;
                }
            }

            // ---- Trial end detection (nonzero -> phase 0) ----
            // Appends this trial's sync mean to file.
            if (_prevPhase != 0 && latest.phase == 0)
            {
                if (_syncStats.n > 0)
                {
                    AppendSyncToFile(_syncStats.mean, _syncStats.max);
                    Debug.Log($"[Sync] Trial complete, mean error {_syncStats.mean:F2}° (n={_syncStats.n}) appended to sync_results.csv");
                }
            }

            _prevPhase = latest.phase;

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

    void OnDisable() { FlushTransferLatencyStats(); ClosePort(); }
    void OnDestroy() { FlushTransferLatencyStats(); ClosePort(); }
    void OnApplicationQuit() { FlushTransferLatencyStats(); ClosePort(); }

    // -------------------------------------------------------------------------
    // STEP DETECTION
    // -------------------------------------------------------------------------
    private void CheckStepConditions(float yaw, float pitch, float roll)
    {
        if (instructionManager == null) return;
        int step = instructionManager.currentStepIndex;
        if (step < 0) return;

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
                if (yaw >= yawThreshold - 2f && yaw <= yawThreshold + 2f)
                {
                    _confirmedYaw = yaw;
                    _supineReached = false;
                    Debug.Log($"✔ Step 1: Head turned right (Y:{yaw:F1}°)");
                    instructionManager.CompleteStep();
                }
                else if (yaw > 10f)
                    Debug.Log($"[Step 1] Progress: yaw={yaw:F1}° / {yawThreshold}°");
                break;

            // Step 2: Lie back with head turned right
            case 2:
                CheckDixHallpikeLieBack(2, turnedRight: true);
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
                CheckReturnToNeutral(4, "right", pitch, yaw);
                break;

            // Step 5: Turn head LEFT 45 degrees
            case 5:
                if (yaw <= -yawThreshold + 2f && yaw >= -yawThreshold - 2f)
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
                CheckDixHallpikeLieBack(6, turnedRight: false);
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
                CheckReturnToNeutral(8, "left", pitch, yaw);
                break;

            // Step 9: Diagnosis fired automatically when Step 8 completes
            case 9:
                break;
        }
    }

    // ********* STEP 2/6 - DIX-HALLPIKE LIE-BACK HELPER ***********************
    // Steps 2 and 6 are the same maneuver mirrored left/right; only the yaw
    // sign and step number differ.
    private void CheckDixHallpikeLieBack(int step, bool turnedRight)
    {
        if (_smoothedPitchVelocity > 0f)
            _peakPitchVelocity = Mathf.Max(_peakPitchVelocity, _smoothedPitchVelocity);

        if (!_supineReached)
        {
            if (_curTilt >= supineThreshold)
            {
                _supineReached = true;
                Debug.Log($"[Step {step}] Supine reached (tilt:{_curTilt:F1}°)");
            }
            else if (_curTilt > 10f)
                Debug.Log($"[Step {step}] Reclining... tilt={_curTilt:F1}° / {supineThreshold}°");
            return;
        }

        float target = supineThreshold + hangBelowSupine;
        bool yawHeld = turnedRight
            ? _confirmedYaw >= yawThreshold - 2f
            : _confirmedYaw <= -yawThreshold + 2f;

        if (_curTilt >= target && yawHeld)
        {
            EvaluateLieBackSpeed(_peakPitchVelocity, step);
            _peakPitchVelocity = 0f;
            string side = turnedRight ? "Right" : "Left";
            Debug.Log($"✔ Step {step}: {side} Dix-Hallpike (tilt:{_curTilt:F1}° Y:{_confirmedYaw:F1}°)");
            instructionManager.CompleteStep();
        }
        else if (_curTilt > 5f)
            Debug.Log($"[Step {step}] Head hang: tilt={_curTilt:F1}° / {target:F1}° yaw={_confirmedYaw:F1}°");
    }

    // ********* STEP 4/8 - RETURN TO NEUTRAL HELPER ****************************
    // Steps 4 and 8 are the same "settle back to neutral" check after each
    // side's hold; only which side just ran and the step number differ.
    private void CheckReturnToNeutral(int step, string sideJustDone, float pitch, float yaw)
    {
        if (Mathf.Abs(pitch) < 15f && Mathf.Abs(yaw) < 20f)
        {
            bool settled = activeBppvSide != sideJustDone || CurrentPhase == 0;
            if (settled)
            {
                Debug.Log($"✔ Step {step}: Neutral after {sideJustDone} (P:{pitch:F1}°)");
                ResetHoldState();
                instructionManager.CompleteStep();
            }
            else
                Debug.Log($"[Step {step}] Waiting for motor reversal. Phase:{CurrentPhase}");
        }
        else if (Mathf.Abs(pitch) < 30f)
            Debug.Log($"[Step {step}] Returning... pitch={pitch:F1}°");
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
                string command = activeBppvSide == "left" ? CMD_TRIGGER_LEFT : CMD_TRIGGER_RIGHT;
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
    // PARSE - H:yaw,pitch,roll|T:torsion,V:vertical,H:horizontal,P:phase,S:side,X:tilt,TS:millis
    // (TS: field is optional - old Arduino firmware without it still parses fine,
    // it just won't feed the one-way transfer-latency measurement, which is used only for evaluation.)
    // ------------------------------------------------------------------
    private bool TryParseLine(string line, out CombinedFrame frame)
    {
        frame = default;
        frame.arduinoTs = -1;
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
            if (e.Length < 6) return false;
            frame.torsion = float.Parse(e[0].Split(':')[1], culture);
            frame.vertical = float.Parse(e[1].Split(':')[1], culture);
            frame.horizontal = float.Parse(e[2].Split(':')[1], culture);
            frame.phase = int.Parse(e[3].Split(':')[1]);
            // e[4] "S:side" is intentionally skipped - side comes from activeBppvSide, not the wire.
            frame.tilt = float.Parse(e[5].Split(':')[1], culture);

            // Optional 7th field: TS:<arduino millis()>
            if (e.Length >= 7)
            {
                string[] tsParts = e[6].Split(':');
                if (tsParts.Length >= 2 && long.TryParse(tsParts[1], out long ts))
                    frame.arduinoTs = ts;
            }

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
        // Record the send time for trigger commands only, used for latency measurement
        if (cmd == CMD_TRIGGER_RIGHT || cmd == CMD_TRIGGER_LEFT || cmd == CMD_TRIGGER)
        {
            _lastCommandSentTime = Time.realtimeSinceStartup;
            _awaitingLatency = true;
        }

        // Lock only for the Write — never during ReadLine
        lock (_serialLock)
        {
            if (_serial == null || !_serial.IsOpen)
            {
                Debug.LogWarning("[Serial] Cannot send - port not open.");
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
        _supineReached = false;
        ResetHoldState();
    }

    // ************ EVALUATION REPORTING *****************************************
    // -------------------------------------------------------------------------
    // LATENCY / SYNC FILE APPENDING
    // -------------------------------------------------------------------------
    // One line per trial, written relative to Unity's working directory
    // (project root in the editor). Files persist across separate Play
    // sessions, so you do not need to keep the same session open across
    // all your trials.
    private void AppendLatencyToFile(float latencyMs)
    {
        System.IO.File.AppendAllText("latency_results.csv", $"{latencyMs:F2}\n");
    }

    private void AppendSyncToFile(float meanSyncError, float maxSyncError)
    {
        System.IO.File.AppendAllText("sync_results.csv", $"{meanSyncError:F2},{maxSyncError:F2}\n");
    }

    // One line per Play session (not per trial, and not per sample - that
    // would be hundreds of disk writes a second). Same two-column shape as
    // sync_results.csv (mean,max).
    private void AppendTransferLatencyToFile(float meanMs, float maxMs)
    {
        System.IO.File.AppendAllText("transfer_latency_results.csv", $"{meanMs:F2},{maxMs:F2}\n");
    }

    private void FlushTransferLatencyStats()
    {
        if (_transferLatencyFlushed) return;
        var s = LatencyMonitor.GetStats();
        if (s.n == 0) return; // never got a TS: field, or Arduino not reflashed yet
        AppendTransferLatencyToFile(s.mean, s.max);
        Debug.Log($"[TransferLatency] Session complete, mean {s.mean:F2}ms max {s.max}ms (n={s.n}) appended to transfer_latency_results.csv");
        _transferLatencyFlushed = true;
    }

    // -------------------------------------------------------------------------
    // FINAL AGGREGATE REPORT - Just used for easier reporting.
    // -------------------------------------------------------------------------
    // Call once, after all trials across all sessions are done. Reads both
    // CSV files fresh off disk, computes mean/SD/max over every row ever
    // written, and prints two ready-to-paste LaTeX blocks (pgfplots
    // coordinates + a draft results-table row) to the console.
    private void PrintFinalAggregateReport()
    {
        FlushTransferLatencyStats(); // make sure this session's data is on disk too

        Debug.Log("###### FINAL AGGREGATE REPORT ######");
        PrintAggregateFromFile("latency_results.csv", "ms", "$<$100ms", "Latency (command round-trip)", false);
        PrintAggregateFromFile("transfer_latency_results.csv", "ms", "$<$100ms", "Latency (one-way data transfer)", true);
        PrintAggregateFromFile("sync_results.csv", "\\textdegree", "$\\leq$2\\textdegree", "Positional sync", true);
    }

    private void PrintAggregateFromFile(string path, string unit, string target, string rowLabel, bool hasMaxColumn = false)
    {
        if (!System.IO.File.Exists(path))
        {
            Debug.LogWarning($"[FinalReport] {path} not found yet.");
            return;
        }

        string[] lines = System.IO.File.ReadAllLines(path);
        List<float> values = new List<float>();
        List<float> maxValues = new List<float>();

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] parts = line.Split(',');
            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v))
                values.Add(v);
            if (hasMaxColumn && parts.Length > 1 &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float vm))
                maxValues.Add(vm);
        }

        if (values.Count == 0) { Debug.LogWarning($"[FinalReport] No valid rows in {path}."); return; }

        float mean = 0f; foreach (float v in values) mean += v; mean /= values.Count;
        float sumSq = 0f; foreach (float v in values) sumSq += (v - mean) * (v - mean);
        float sd = values.Count > 1 ? Mathf.Sqrt(sumSq / (values.Count - 1)) : 0f;
        float max = float.MinValue; foreach (float v in values) if (v > max) max = v;

        string worstCasePeak = "";
        if (hasMaxColumn && maxValues.Count > 0)
        {
            float peakOfPeaks = float.MinValue;
            foreach (float vm in maxValues) if (vm > peakOfPeaks) peakOfPeaks = vm;
            worstCasePeak = $"  worst_single_sample_peak={peakOfPeaks:F2}{unit}";
        }

        Debug.Log($"========== {rowLabel.ToUpper()}, n={values.Count} ==========");
        Debug.Log($"mean_of_trial_means={mean:F2}{unit}  sd={sd:F2}{worstCasePeak}");

        string coords = "";
        for (int i = 0; i < values.Count; i++) coords += $"({i + 1},{values[i]:F1}) ";
        Debug.Log("\\addplot coordinates {" + coords + "};");

        string peakNote = hasMaxColumn && maxValues.Count > 0
            ? $" (worst-case single-sample peak {maxValues[maxValues.Count - 1]:F1}{unit} shown separately)"
            : "";
        Debug.Log($"{rowLabel} & {target} & {mean:F1}{unit} (SD {sd:F1}){peakNote} & \\\\");
    }
}