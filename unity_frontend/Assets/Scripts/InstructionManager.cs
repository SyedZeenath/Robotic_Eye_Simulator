using UnityEngine;
using System.Collections.Generic;
using TMPro;
using System.Collections;

public class InstructionManager : MonoBehaviour
{
    [Header("Steps")]
    public List<InstructionStep> steps = new List<InstructionStep>();
    public int currentStepIndex = -1;

    [Header("References")]
    public TTS_Script tts;
    public GameObject feedbackPanel;
    public TextMeshProUGUI instructionTextUI;
    private Patient currentPatient;

    void Start()
    {
        // Define all 5 steps (0-4) for the BPPV Dix-Hallpike maneuver
        if (steps.Count == 0)
        {
            Debug.LogWarning("InstructionManager: No steps defined! Add steps in Inspector.");
            // Or initialize programmatically:
            /*
            steps.Add(new InstructionStep { 
                instructionText = "Please sit upright with your head in a neutral position.",
                isCompleted = false 
            });
            steps.Add(new InstructionStep { 
                instructionText = "Turn your head 45 degrees to the right.",
                isCompleted = false 
            });
            steps.Add(new InstructionStep { 
                instructionText = "Now lie down on your back while keeping your head turned.",
                isCompleted = false 
            });
            steps.Add(new InstructionStep { 
                instructionText = "Hold this position. Observe any eye movements or dizziness.",
                isCompleted = false 
            });
            steps.Add(new InstructionStep { 
                instructionText = "Return to a sitting position with your head facing forward.",
                isCompleted = false 
            });
            */
        }
    }

    public void UpdateUI()
    {
        if (currentStepIndex >= steps.Count) return;

        if (currentStepIndex >= 0)
        {
            instructionTextUI.text = $"Step {currentStepIndex + 1}: "
                                     + steps[currentStepIndex].instructionText;
        }
    }

    public void welcomeMessage()
    {
        tts.SpeakOnDemand("Starting BPPV simulation. Please select a patient to begin.");
    }

    public void SpeakPatientProfile(Patient patient)
    {
        currentPatient = patient;
        string profileDetails = $"Patient Name {patient.name}, Age {patient.age}, "
                           + $"Neck Stiffness: {patient.neckStiffness}";
        
        instructionTextUI.text = $"Name: {currentPatient.name}\nAge: {currentPatient.age}\n"
                                     + $"Neck Stiffness: {currentPatient.neckStiffness}\n\n";

        tts.SpeakOnDemand(profileDetails);
        StartCoroutine(MoveToStep0());  
    }

    IEnumerator MoveToStep0()
    {
        // Wait for patient profile TTS to finish
        yield return new WaitWhile(() => tts.IsSpeaking);

        // Advance to Step 0
        currentStepIndex = 0;
        UpdateUI();
        SpeakCurrentStep();
        Debug.Log("[InstructionManager] Advanced to Step 0");
    }

    public void SpeakCurrentStep()
    {
        if (currentStepIndex < 0 || currentStepIndex >= steps.Count) return;

        string stepInstruction = $"Step {currentStepIndex + 1}: {steps[currentStepIndex].instructionText}";
        tts.SpeakOnDemand(stepInstruction);
        Debug.Log($"[InstructionManager] Speaking: {stepInstruction}");
    }
    
    // ************************************************************************
    // Called by ArduinoHeadTracker when a step's physical condition is met
    // ************************************************************************
    public void CompleteStepFromArduino()
    {   
        if (currentStepIndex < 0) return; // not started yet
        if (SimulationUI.Instance.patientModel == null) return; // no patient selected
        if (!SimulationUI.Instance.patientModel.activeSelf) return; // simulation not active   
        // GATE: Block if TTS is still speaking the current instruction
        if (tts != null && tts.IsSpeaking) 
        {
            Debug.Log($"[InstructionManager] Cannot advance - TTS still speaking Step {currentStepIndex + 1}");
            return;
        }

        Debug.Log($"[InstructionManager] Step {currentStepIndex} completed!");

        if (currentStepIndex < steps.Count)
        {
            steps[currentStepIndex].isCompleted = true;
            currentStepIndex++;

            if (currentStepIndex < steps.Count)
            {
                // Advance to next step and speak instruction immediately
                UpdateUI();
                SpeakCurrentStep();
                Debug.Log($"[InstructionManager] Advanced to Step {currentStepIndex}");
            }
            else
            {
                                
                // All steps completed
                instructionTextUI.text = "Simulation Complete. Please give your diagnosis.";
                tts.SpeakOnDemand("Simulation Complete. Please give your diagnosis.");
                Debug.Log("[InstructionManager] Simulation complete - triggering diagnosis flow");

                // Hand off to diagnosis controller instead of showing static panel
                DiagnosisFeedbackController diagCtrl = FindObjectOfType<DiagnosisFeedbackController>();
                if (diagCtrl != null)
                    diagCtrl.TriggerDiagnosisFlow();
            }
        }
    }

    void Update()
    {
        // Manual override with Space key for testing/debugging
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Debug.Log("[InstructionManager] Manual step completion via Space key");
            CompleteStepFromArduino();
        }
    }
}
