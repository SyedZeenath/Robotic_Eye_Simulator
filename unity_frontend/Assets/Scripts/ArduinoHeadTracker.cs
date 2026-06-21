using System.IO.Ports;
using UnityEngine;

public class ArduinoHeadTracker : MonoBehaviour
{
    private float yawThreshold = 45f;
    private float pitchThreshold = 20f;
    public string portName = "COM3";
    public int baudRate = 115200;

    public InstructionManager instructionManager;
    public RealisticEyeController eyeController;

    private SerialPort serial;
    private Quaternion targetRotation;
    public float smoothSpeed = 5f;

    // Latest valid sensor reading
    private float curRelYaw   = 0f;
    private float curRelPitch = 0f;
    private float curRelRoll  = 0f;
    private bool  hasFreshReading = false;

    // Step 3 tracking
    private bool nystagmusTriggered = false;
    private float nystagmusTimer = 0f;
    private float nystagmusDuration = 10f;  // Typical BPPV nystagmus lasts ~10 seconds

    void Start()
    {
        serial = new SerialPort(portName, baudRate);
        serial.ReadTimeout = 100;  // 100ms timeout
        serial.DtrEnable = true;   // Enable Data Terminal Ready
        
        try
        {
            serial.Open();
            
            // CRITICAL: Flush any stale data from buffer
            System.Threading.Thread.Sleep(100);  // Give Arduino time to reset
            serial.DiscardInBuffer();
            serial.DiscardOutBuffer();
            
            Debug.Log($"✓ Serial connected to {portName} at {baudRate} baud");
            Debug.Log("⏱ Waiting for Arduino calibration (15 seconds)...");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ Could not open serial port {portName}: {e.Message}");
        }
    }

    void Update()
    {
        if (serial == null || !serial.IsOpen) return;

        // ******** Serial read ************************************
        hasFreshReading = false;
        try
        {
            // Only read if data is available
            if (serial.BytesToRead > 0)
            {
                string data = serial.ReadLine().Trim();
                
                // Skip Arduino debug messages
                if (string.IsNullOrEmpty(data) || 
                    data.Contains("Calibration") || 
                    data.Contains("MPU") ||
                    data.Contains("DMP") ||
                    data.Contains("Offsets"))
                {
                    Debug.Log($"[Arduino] {data}");  // Show Arduino messages
                    return;
                }
                
                // Parse sensor values
                string[] vals = data.Split(',');
                if (vals.Length == 3)
                {
                    // Arduino already sends calibrated values!
                    curRelYaw = float.Parse(vals[0]);
                    curRelPitch = float.Parse(vals[1]);
                    curRelRoll = float.Parse(vals[2]);

                    targetRotation = Quaternion.Euler(curRelPitch, curRelYaw, -curRelRoll);
                    hasFreshReading = true;

                    Debug.Log($"Yaw:{curRelYaw:F1}  Pitch:{curRelPitch:F1}  Roll:{curRelRoll:F1}");
                }
                else
                {
                    Debug.LogWarning($"⚠ Invalid data format: '{data}' (expected 3 values)");
                }
            }
        }
        catch (System.TimeoutException)
        {
            // Normal - no data available this frame
        }
        catch (System.FormatException e)
        {
            Debug.LogWarning($"⚠ Parse error: {e.Message}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ Serial read error: {e.Message}");
            
            // Try to reconnect if connection is lost
            if (!serial.IsOpen)
            {
                TryReconnect();
            }
        }

        // **** Instruction detection *****************************************
        if (hasFreshReading)
            CheckStepConditions(curRelYaw, curRelPitch, curRelRoll);

        // **** Smooth head motion ********************************************
        transform.rotation = Quaternion.Slerp(
            transform.rotation, targetRotation, Time.deltaTime * smoothSpeed);
    }

    private void TryReconnect()
    {
        try
        {
            Debug.Log("🔄 Attempting to reconnect...");
            serial.Close();
            System.Threading.Thread.Sleep(500);
            serial.Open();
            serial.DiscardInBuffer();
            serial.DiscardOutBuffer();
            Debug.Log("✓ Reconnected!");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ Reconnect failed: {e.Message}");
        }
    }


    // CONDITION-BASED STEP DETECTION
    // Each step advances ONLY when its specific physical condition is met
    private void CheckStepConditions(float yaw, float pitch, float roll)
    {
        if (instructionManager == null) return;

        int step = instructionManager.currentStepIndex;
        if (step < 0) return; // patient profile not done yet

        // CRITICAL GATE: Don't advance while TTS is speaking 
        // This ensures user hears the full instruction before we check for completion
        if (instructionManager.tts != null && instructionManager.tts.IsSpeaking)
        {
            return;  // Wait for instruction to finish speaking
        }

        // Check physical conditions based on current step
        switch (step)
        {
            // ------------------------------------------------------------------
            // Step 0: Confirm neutral position
            // Condition: Head near center (±10°)
            // ------------------------------------------------------------------
            case 0:
                if (Mathf.Abs(yaw) < 10f && Mathf.Abs(pitch) < 10f)
                {
                    Debug.Log($"✔ Step 0 complete: Neutral position confirmed (yaw:{yaw:F1}°, pitch:{pitch:F1}°)");
                    instructionManager.CompleteStepFromArduino();
                }
                break;

            // ------------------------------------------------------------------
            // Step 1: Turn head 45° to the right
            // Condition: Yaw >= 45° (absolute)
            // ------------------------------------------------------------------
            case 1:
                if (yaw >= yawThreshold)
                {
                    Debug.Log($"✔ Step 1 complete: Head rotated 45° (yaw:{yaw:F1}°)");
                    instructionManager.CompleteStepFromArduino();
                }
                else if (yaw > 10f) // Show progress
                {
                    Debug.Log($"[Step 1] Progress: yaw={yaw:F1}° (need {yawThreshold}°)");
                }
                break;

            // ------------------------------------------------------------------
            // Step 2: Lie down (supine position)
            // Condition: Pitch >= 20° (absolute) AND yaw still >= 45°
            // ------------------------------------------------------------------
            case 2:
                if (pitch >= pitchThreshold && yaw >= yawThreshold)
                {
                    Debug.Log($"✔ Step 2 complete: Supine position (pitch:{pitch:F1}°, yaw:{yaw:F1}°)");
                    instructionManager.CompleteStepFromArduino();
                }
                else if (pitch > 5f)  // Show progress
                {
                    Debug.Log($"[Step 2] Progress: pitch={pitch:F1}° (need {pitchThreshold}°), yaw={yaw:F1}°");
                }
                break;

            // ------------------------------------------------------------------
            // Step 3: Hold position and observe nystagmus
            // Condition: Maintain supine position → trigger eye movement
            //            → wait for nystagmus to complete (~10s)
            // ------------------------------------------------------------------
            case 3:
                // Check if still in supine position
                if (pitch >= pitchThreshold && yaw >= yawThreshold)
                {
                    // Trigger nystagmus after 2.5s hold (matches latency period)
                    if (!nystagmusTriggered)
                    {
                        nystagmusTimer += Time.deltaTime;
                        if (nystagmusTimer >= 2.5f)
                        {
                            nystagmusTriggered = true;
                            nystagmusTimer = 0f;  // Reset to track nystagmus duration
                            Debug.Log("✔ Latency complete → triggering BPPV nystagmus");
                            eyeController?.ApplySimulationData(
                                new Vector3(pitch, yaw, roll), 1, 1.0f, 2.0f);
                        }
                        else
                        {
                            Debug.Log($"[Step 3] Holding position... {nystagmusTimer:F1}s / 2.5s");
                        }
                    }
                    else
                    {
                        // Nystagmus is active - wait for it to complete
                        nystagmusTimer += Time.deltaTime;
                        if (nystagmusTimer >= nystagmusDuration)
                        {
                            Debug.Log($"✔ Step 3 complete: Nystagmus completed ({nystagmusTimer:F1}s)");
                            instructionManager.CompleteStepFromArduino();
                        }
                        else
                        {
                            Debug.Log($"[Step 3] Nystagmus active... {nystagmusTimer:F1}s / {nystagmusDuration}s");
                        }
                    }
                }
                else
                {
                    // Patient moved out of position - reset
                    if (nystagmusTriggered)
                    {
                        Debug.LogWarning("Patient moved out of position - nystagmus observation interrupted");
                    }
                    nystagmusTimer = 0f;
                    nystagmusTriggered = false;
                }
                break;

            // ------------------------------------------------------------------
            // Step 4: Return to neutral sitting position
            // Condition: Pitch back to upright (yaw ignored due to drift)
            // ------------------------------------------------------------------
            case 4:
                if (Mathf.Abs(pitch) < 15f)  // Only check pitch - yaw drifts
                {
                    Debug.Log($"✔ Step 4 complete: Returned to upright (pitch:{pitch:F1}°)");
                    instructionManager.CompleteStepFromArduino();
                }
                else if (Mathf.Abs(pitch) < 30f)  // Show progress
                {
                    Debug.Log($"[Step 4] Returning... pitch={pitch:F1}° (need < 15°)");
                }
                break;
        }
    }

    public void ResetBPPVState()
    {
        hasFreshReading  = false;
        nystagmusTriggered = false;
        nystagmusTimer   = 0f;
    }

    void OnApplicationQuit()
    {
        if (serial != null && serial.IsOpen) serial.Close();
    }
}