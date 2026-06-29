using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

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
        InitialiseSteps();
    }

    // Called programmatically so text is always consistent
    // Steps 1-4 test right side, steps 5-8 test left side
    void InitialiseSteps()
    {
        steps.Clear();

        steps.Add(new InstructionStep {
            instructionText = "Please sit upright with your head in a neutral position, looking straight ahead.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Turn the patient's head 45 degrees to the RIGHT.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Quickly lie the patient back so their head hangs 20 degrees below the table, head still turned right.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Hold this position and observe the patient's eyes carefully for any nystagmus.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Return the patient to the upright sitting position.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Wait a moment, then turn the patient's head 45 degrees to the LEFT.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Quickly lie the patient back so their head hangs 20 degrees below the table, head still turned left.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Hold this position and observe the patient's eyes carefully for any nystagmus.",
            isCompleted = false
        });
        steps.Add(new InstructionStep {
            instructionText = "Return the patient to the upright sitting position.",
            isCompleted = false
        });
    }

    public void UpdateUI()
    {
        if (currentStepIndex < 0 || currentStepIndex >= steps.Count) return;
        instructionTextUI.text = $"Step {currentStepIndex + 1}: "
                               + steps[currentStepIndex].instructionText;
    }

    public void welcomeMessage()
    {
        tts.SpeakOnDemand("Starting Dix-Hallpike simulation. Please select a patient to begin.");
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
        yield return new WaitWhile(() => tts.IsSpeaking);
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

    public void CompleteStepFromArduino()
    {
        if (currentStepIndex < 0) return;
        if (SimulationUI.Instance.patientModel == null) return;
        if (!SimulationUI.Instance.patientModel.activeSelf) return;

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
                UpdateUI();
                SpeakCurrentStep();
                Debug.Log($"[InstructionManager] Advanced to Step {currentStepIndex}");
            }
            else
            {
                // All 10 steps complete, trigger diagnosis flow
                instructionTextUI.text = "Assessment complete. Please give your diagnosis.";
                Debug.Log("[InstructionManager] All steps complete - triggering diagnosis flow");

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
            Debug.Log("[InstructionManager] Manual step via Space");
            CompleteStepFromArduino();
        }
    }
}