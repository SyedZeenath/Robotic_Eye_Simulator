using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class DiagnosisFeedbackController : MonoBehaviour
{
    [Header("Dependencies")]
    public TTS_Script tts;
    public STT_Script stt;
    public MicrophoneRecorder recorder;

    [Header("Feedback UI")]
    public GameObject feedbackPanel;
    public TextMeshProUGUI feedbackTitleText;
    public TextMeshProUGUI feedbackBodyText;
    
    [Header("Recording Indicator")]
    public RecordingIndicator recordingIndicator;

    [Header("Timing")]
    public float postTTSDelay = 0.8f;

    // Each entry: keywords that map to a named BPPV type, whether it is correct,
    // and the spoken + written explanation
    class BPPVType
    {
        public string label; // display name
        public string[] keywords; // any of these in transcript = this type
        public bool isCorrect;
        public string explanation; // shown in feedback body
        public string spoken; // spoken by TTS
    }

    // Update this List's isCorrect=True/False field to change which answer is considered correct 
    // when there are multiple valid BPPV types implemented. For now, any other answer is considered incorrect.
    static readonly List<BPPVType> KnownTypes = new List<BPPVType>
    {
        new BPPVType {
            label = "Posterior canal BPPV",
            keywords = new[]{ "posterior", "bppv", "torsional", "upbeat", "benign paroxysmal" },
            isCorrect = true,
            explanation =
                "Correct. This is right posterior canal BPPV.\n\n"
              + "Displaced otoconia from the utricle settle in the posterior semicircular canal. "
              + "When the head moves into the Dix-Hallpike position, the debris shifts, deflecting "
              + "the cupula and triggering the characteristic upbeat-torsional nystagmus you observed, "
              + "with a short latency, crescendo-decrescendo pattern, and reversal on return to sitting.",
            spoken =
                "Correct. It is posterior canal BPPV. "
              + "Displaced otoconia in the posterior canal cause the upbeat torsional nystagmus "
              + "seen during the Dix-Hallpike maneuver."
        },

        new BPPVType {
            label = "Horizontal canal BPPV",
            keywords = new[]{ "horizontal", "lateral", "geotropic", "apogeotropic", "canal switch" },
            isCorrect = false,
            explanation =
                "Not quite. Horizontal canal BPPV is a valid type but not what was simulated here.\n\n"
              + "Horizontal canal BPPV produces a purely horizontal direction-changing nystagmus, "
              + "best elicited by the roll test rather than Dix-Hallpike. "
              + "The nystagmus you observed - upbeat with a torsional component - is the hallmark "
              + "of posterior canal involvement, not horizontal.",
            spoken =
                "Not quite. Horizontal canal BPPV produces a purely horizontal nystagmus in the roll test. "
              + "The upbeat torsional nystagmus you observed points to the posterior canal."
        },

        new BPPVType {
            label = "Anterior canal BPPV",
            keywords = new[]{ "anterior", "superior", "downbeat" },
            isCorrect = false,
            explanation =
                "Not quite. Anterior canal BPPV is rare and was not simulated here.\n\n"
              + "Anterior canal BPPV produces a downbeat nystagmus with a torsional component "
              + "during Dix-Hallpike, the opposite vertical direction to what you observed. "
              + "The upbeat torsional pattern here is consistent with posterior canal BPPV.",
            spoken =
                "Not quite. Anterior canal BPPV produces downbeat nystagmus, the opposite of what you saw. "
              + "The correct answer is posterior canal BPPV."
        },

        new BPPVType {
            label = "Cupulolithiasis",
            keywords = new[]{ "cupulo", "cupulolithiasis", "heavy cupula" },
            isCorrect = false,
            explanation =
                "Partially correct thinking, but cupulolithiasis is a subtype mechanism rather than a canal diagnosis.\n\n"
              + "In cupulolithiasis, debris adheres to the cupula rather than floating freely (canalithiasis). "
              + "It produces a persistent nystagmus without the typical crescendo-decrescendo pattern. "
              + "The simulated pattern, with clear latency and decay matches canalithiasis of the posterior canal.",
            spoken =
                "That describes the debris mechanism rather than the canal. "
              + "The simulated pattern matches canalithiasis of the posterior canal."
        }
    };

    // Fallback if nothing matched
    const string FallbackTitle = "Unclear diagnosis";
    const string FallbackBody =
        "The response did not match a recognised BPPV type.\n\n"
      + "The correct answer is posterior canal BPPV (right side). "
      + "Displaced otoconia in the posterior semicircular canal cause the upbeat-torsional "
      + "nystagmus with the latency and crescendo-decrescendo pattern shown in the simulation.";
    const string FallbackSpoken =
        "The correct diagnosis is right posterior canal BPPV. "
      + "The upbeat torsional nystagmus with latency and decay is the clinical signature of "
      + "posterior canal canalithiasis.";


    byte[] _lastRecording;

    void Awake()
    {
        recorder.OnRecordingComplete += OnRecordingDone;
    }

    public void TriggerDiagnosisFlow()
    {
        StartCoroutine(DiagnosisCoroutine());
    }

    IEnumerator DiagnosisCoroutine()
    {
        feedbackTitleText.text = "Listening...";
        feedbackBodyText.text  = "Please speak your diagnosis clearly.";
        feedbackTitleText.color = new Color(1f, 1f, 1f);  // white for listening
        feedbackPanel.SetActive(true);

        tts.SpeakOnDemand("What type of BPPV is your diagnosis?");
        yield return new WaitWhile(() => tts.IsSpeaking);
        yield return new WaitForSeconds(postTTSDelay);

        // Start recording indicator then mic
        if (recordingIndicator != null)
            recordingIndicator.StartRecording();

        recorder.StartRecording();
    }

    void OnRecordingDone(byte[] wav)
    {
        if (recordingIndicator != null)
            recordingIndicator.StopRecording();

        // Update panel text while STT runs
        feedbackTitleText.text  = "Processing...";
        feedbackBodyText.text   = "Analysing your response.";

        _lastRecording = wav;
        StartCoroutine(TranscribeAndEvaluate(wav));
    }

    IEnumerator TranscribeAndEvaluate(byte[] wav)
    {
        string recognized = "";
        yield return StartCoroutine(stt.Recognize(wav, r => recognized = r));

        if (string.IsNullOrEmpty(recognized))
        {
            ShowFeedback("No speech detected",
                FallbackBody, false);
            tts.SpeakOnDemand("No response detected. " + FallbackSpoken);
            yield break;
        }

        Debug.Log("[DiagnosisFeedback] Evaluating: " + recognized);
        Evaluate(recognized);
    }

    void Evaluate(string text)
    {
        BPPVType matched = null;

        foreach (BPPVType type in KnownTypes)
        {
            foreach (string kw in type.keywords)
            {
                if (text.Contains(kw))
                {
                    matched = type;
                    break;
                }
            }
            if (matched != null) break;
        }

        if (matched != null)
        {
            ShowFeedback(matched.label, matched.explanation, matched.isCorrect);
            tts.SpeakOnDemand(matched.spoken);
        }
        else
        {
            ShowFeedback(FallbackTitle, FallbackBody, false);
            tts.SpeakOnDemand(FallbackSpoken);
        }
    }

    void ShowFeedback(string title, string body, bool correct)
    {
        feedbackTitleText.text = title;
        feedbackBodyText.text = body;
        feedbackTitleText.color = correct
            ? new Color(0.11f, 0.60f, 0.35f) // green
            : new Color(0.78f, 0.20f, 0.20f); // red

    }
}