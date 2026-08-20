/*
  SimulationUI.cs

  Supports:
    - Serial mode (Arduino → Serial → Unity direct)
*/

using UnityEngine;
using UnityEngine.UI;
using TMPro;

[System.Serializable]
public struct SimpleEyeData
{
    public Vector3 headRotation;
    public int activeCanal;
    public float nystagmusAmplitude;
    public float nystagmusFrequency;
}

public class SimulationUI : MonoBehaviour
{
    [Header("UI")]
    public Button startButton;
    public TextMeshProUGUI titleText;

    [Header("Phase Display (assign a TMP text in Inspector)")]
    public TextMeshProUGUI phaseLabel; // shows current BPPV phase name
    public TextMeshProUGUI connectionStatus; // shows Serial connected / disconnected

    [Header("BPPV Trigger Buttons (optional)")]
    public Button rightBPPVButton; // sends "R" to Arduino
    public Button leftBPPVButton; // sends "L" to Arduino
    public Button neutralButton; // sends "N" to Arduino

    public InstructionManager instructionManager;

    [Header("Panels")]
    public GameObject instructionPanel;
    public GameObject feedbackPanel;
    public GameObject patientsPanel;

    [Header("Unity Model")]
    public GameObject patientModel;
    public RealisticEyeController eyeController;

    [Header("Serial Reader")]
    public ArduinoSerialReader serialReader; // assign in Inspector

    // ****** Phase name lookup *******************************************
    private static readonly string[] PHASE_NAMES = {
        "Neutral",
        "Phase 1 - Latency",
        "Phase 2 - Crescendo",
        "Phase 3 - Nystagmus Beats",
        "Phase 4 - Decrescendo",
        "Phase 5 - Reversal",
    };

    public static SimulationUI Instance;

    void Start()
    {
        Instance = this;

        patientModel.SetActive(false);
        instructionPanel.SetActive(false);
        feedbackPanel.SetActive(false);
        patientsPanel.SetActive(false);

        startButton.onClick.AddListener(OnStartClicked);

        // Wire up BPPV trigger buttons if assigned
        if (rightBPPVButton != null)
            rightBPPVButton.onClick.AddListener(() => SendArduinoCommand("R"));

        if (leftBPPVButton != null)
            leftBPPVButton.onClick.AddListener(() => SendArduinoCommand("L"));

        if (neutralButton != null)
            neutralButton.onClick.AddListener(() => SendArduinoCommand("N"));
    }

    void Update()
    {
        UpdatePhaseLabel();
        UpdateConnectionStatus();
    }

    // ****** Update phase label text from ArduinoSerialReader current phase ******
    void UpdatePhaseLabel()
    {
        if (phaseLabel == null || serialReader == null) return;

        int phase = serialReader.CurrentPhase;
        string name = (phase >= 0 && phase < PHASE_NAMES.Length)
            ? PHASE_NAMES[phase]
            : "Unknown";

        phaseLabel.text = name;
    }

    // *********** Show Serial connection status in UI ****************************
    void UpdateConnectionStatus()
    {
        if (connectionStatus == null || serialReader == null) return;

        if (serialReader.IsReceivingData)
        {
            connectionStatus.text = "Arduino: Connected";
            connectionStatus.color = Color.green;
        }
        else
        {
            connectionStatus.text = "Arduino: No Data";
            connectionStatus.color = Color.red;
        }
    }

    // ****** Send command to Arduino via Serial ****************************
    void SendArduinoCommand(string cmd)
    {
        if (serialReader != null)
            serialReader.SendCommand(cmd);
        else
            Debug.LogWarning("[SimulationUI] SerialReader not assigned.");
    }

    // ****** Handle start button click *************************************
    void OnStartClicked()
    {
        patientsPanel.SetActive(true);
        startButton.gameObject.SetActive(false);
        titleText.gameObject.SetActive(false);

        if (instructionManager != null)
            instructionManager.welcomeMessage();

        Debug.Log("Patients Loaded!");
    }

    // ****** Select a patient *********************************************
    public void SelectPatient(Patient patient)
    {
        Debug.Log("Patient Selected: " + patient.name);

        patientsPanel.SetActive(false);
        patientModel.SetActive(true);
        instructionPanel.SetActive(true);
        feedbackPanel.SetActive(false);

        if (serialReader != null)
            serialReader.SetPatientBPPVInfo(patient.bppvSide, patient.bppvType);

        if (instructionManager != null)
        {
            instructionManager.currentStepIndex = -1;
            instructionManager.SpeakPatientProfile(patient);
        }
    }
}