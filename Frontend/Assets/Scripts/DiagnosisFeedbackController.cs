using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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

    // Each entry: keywords that SPECIFICALLY identify a named BPPV canal type,
    // whether it is correct, and the spoken + written explanation.
    //
    // IMPORTANT: Keywords.here must only contain terms that uniquely point to
    // THIS canal. Generic BPPV-acknowledgement terms ("bppv", "benign
    // paroxysmal...") do not belong here -- they live in
    // GenericAcknowledgementKeywords below, because a trainee who only says
    // "it's BPPV" has not actually named a canal and should not be silently
    // credited with one.
    public class BPPVType
    {
        public string Label;
        public string[] Keywords;
        public bool IsCorrect;
        public string Explanation;
        public string Spoken;
    }

    // Update this List's IsCorrect field to change which answer is considered
    // correct when there are multiple valid BPPV types implemented. For now,
    // any other answer is considered incorrect.
    static readonly List<BPPVType> KnownTypes = new List<BPPVType>
    {
        new BPPVType {
            Label = "Posterior canal BPPV",
            Keywords = new[]{ "posterior", "torsional", "upbeat" },
            IsCorrect = true,
            Explanation =
                "Correct. This is posterior canal BPPV.\n\n"
              + "Displaced otoconia from the utricle settle in the posterior semicircular canal. "
              + "When the head moves into the Dix-Hallpike position, the debris shifts, deflecting "
              + "the cupula and triggering the characteristic upbeat-torsional nystagmus you observed, "
              + "with a short latency, crescendo-decrescendo pattern, and reversal on return to sitting.",
            Spoken =
                "Correct. It is posterior canal BPPV. "
              + "Displaced otoconia in the posterior canal cause the upbeat torsional nystagmus "
              + "seen during the Dix-Hallpike maneuver."
        },

        new BPPVType {
            Label = "Horizontal canal BPPV",
            Keywords = new[]{ "horizontal", "lateral", "geotropic", "apogeotropic", "canal switch" },
            IsCorrect = false,
            Explanation =
                "Not quite. Horizontal canal BPPV is a valid type but not what was simulated here.\n\n"
              + "Horizontal canal BPPV produces a purely horizontal direction-changing nystagmus, "
              + "best elicited by the roll test rather than Dix-Hallpike. "
              + "The nystagmus you observed - upbeat with a torsional component - is the hallmark "
              + "of posterior canal involvement, not horizontal.",
            Spoken =
                "Not quite. Horizontal canal BPPV produces a purely horizontal nystagmus in the roll test. "
              + "The upbeat torsional nystagmus you observed points to the posterior canal."
        },

        new BPPVType {
            Label = "Anterior canal BPPV",
            Keywords = new[]{ "anterior", "superior", "downbeat" },
            IsCorrect = false,
            Explanation =
                "Not quite. Anterior canal BPPV is rare and was not simulated here.\n\n"
              + "Anterior canal BPPV produces a downbeat nystagmus with a torsional component "
              + "during Dix-Hallpike, the opposite vertical direction to what you observed. "
              + "The upbeat torsional pattern here is consistent with posterior canal BPPV.",
            Spoken =
                "Not quite. Anterior canal BPPV produces downbeat nystagmus, the opposite of what you saw. "
              + "The correct answer is posterior canal BPPV."
        },

        new BPPVType {
            Label = "Cupulolithiasis",
            Keywords = new[]{ "cupulo", "cupulolithiasis", "heavy cupula" },
            IsCorrect = false,
            Explanation =
                "Partially correct thinking, but cupulolithiasis is a subtype mechanism rather than a canal diagnosis.\n\n"
              + "In cupulolithiasis, debris adheres to the cupula rather than floating freely (canalithiasis). "
              + "It produces a persistent nystagmus without the typical crescendo-decrescendo pattern. "
              + "The simulated pattern, with clear latency and decay matches canalithiasis of the posterior canal.",
            Spoken =
                "That describes the debris mechanism rather than the canal. "
              + "The simulated pattern matches canalithiasis of the posterior canal."
        }
    };

    // Generic BPPV-acknowledgement terms. These confirm the trainee recognises
    // BPPV as a category but do NOT by themselves name a canal, so they must
    // never be treated as a match for any single BPPVType.
    static readonly string[][] GenericAcknowledgementPhrases =
    {
        new[]{ "bppv" },
        new[]{ "crystals" },
        new[]{ "otoconia" },
        new[]{ "benign", "paroxysmal" },
        new[]{ "benign", "paroxysmal", "positional", "vertigo" },
    };

    // Phrases that negate whatever keyword follows, within the same clause.
    // Contractions are listed both with and without the apostrophe: Google
    // Cloud Speech-to-Text does not always render it depending on model and
    // punctuation settings, and a cue that silently fails to match is worse
    // than no cue at all -- it means the old, pre-fix "always credit the
    // keyword" behavior comes back for exactly that phrasing.
    static readonly string[][] NegationCueSequences =
    {
        new[]{ "not" },
        new[]{ "isn't" }, new[]{ "isnt" },
        new[]{ "wasn't" }, new[]{ "wasnt" },
        new[]{ "doesn't" }, new[]{ "doesnt" },
        new[]{ "don't" }, new[]{ "dont" },
        new[]{ "didn't" }, new[]{ "didnt" },
        new[]{ "rule", "out" },
        new[]{ "ruled", "out" },
        new[]{ "ruling", "out" },
        new[]{ "other", "than" },
        new[]{ "instead", "of" },
        new[]{ "rather", "than" },
        new[]{ "no", "sign", "of" },
        new[]{ "unlikely" },
    };

    // Words/punctuation that end a negation's scope. A cue before one of these
    // does not reach past it (e.g. "not posterior, but horizontal" must not
    // negate "horizontal").
    static readonly HashSet<string> ClauseBoundaryTokens = new HashSet<string>
    {
        "¶", // inserted in place of , . ; : ! ?
        "but", "however", "although", "though"
    };

    // Safety cap only, not a realistic limit: negation scope is bounded by
    // clause structure (ClauseBoundaryTokens), not by a fixed word count.
    // A fixed count of 4 was tried first and was wrong in practice: "I don't
    // think it is a posterior BPPV" puts 5 words between "don't" and
    // "posterior" and was silently graded Correct, since "don't" fell just
    // outside the window. This cap exists only to bound a pathological
    // transcript with no punctuation or contrastive conjunction anywhere in
    // it; for any realistic single-clause spoken answer, the clause boundary
    // is reached first.
    const int NegationWindowWords = 40;

    public enum MatchStatus
    {
        SingleMatch,      // exactly one type affirmed -> grade normally
        MultipleMatches,  // more than one type affirmed -> ambiguous, non-committal answer
        OnlyNegated,      // a type's keyword appeared but only ever negated, no alternative given
        GenericOnly,      // only generic "it's BPPV"-style acknowledgement, no canal named
        NoMatch           // nothing recognisable at all
    }

    public class EvaluationResult
    {
        public MatchStatus Status;
        public BPPVType Matched;            // set when Status == SingleMatch
        public List<BPPVType> Candidates;   // set when Status == MultipleMatches or OnlyNegated
    }

    public static BPPVType CorrectType
    {
        get { return KnownTypes.Find(t => t.IsCorrect); }
    }

    // Fallback if nothing matched at all.
    const string FallbackTitle = "Unclear diagnosis";
    const string FallbackBody =
        "The response did not match a recognised BPPV type.\n\n"
      + "The correct answer is posterior canal BPPV. "
      + "Displaced otoconia in the posterior semicircular canal cause the upbeat-torsional "
      + "nystagmus with the latency and crescendo-decrescendo pattern shown in the simulation.";
    const string FallbackSpoken =
        "The correct diagnosis is posterior canal BPPV. "
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
            ShowFeedback("No speech detected", FallbackBody, false);
            tts.SpeakOnDemand("No response detected. " + FallbackSpoken);
            yield break;
        }

        Debug.Log("[DiagnosisFeedback] Evaluating: " + recognized);
        Evaluate(recognized);
    }

    void Evaluate(string text)
    {
        EvaluationResult result = EvaluateTranscript(text);

        switch (result.Status)
        {
            case MatchStatus.SingleMatch:
                ShowFeedback(result.Matched.Label, result.Matched.Explanation, result.Matched.IsCorrect);
                tts.SpeakOnDemand(result.Matched.Spoken);
                break;

            case MatchStatus.MultipleMatches:
            {
                string names = JoinLabels(result.Candidates);
                string body =
                    "Your answer named more than one possibility (" + names + ").\n\n"
                  + "A diagnosis should commit to a single canal. The correct answer is "
                  + CorrectType.Label + ". " + CorrectType.Explanation;
                string spoken =
                    "Your answer named more than one possibility. A diagnosis should commit to a single canal. "
                  + CorrectType.Spoken;
                ShowFeedback("Ambiguous diagnosis", body, false);
                tts.SpeakOnDemand(spoken);
                break;
            }

            case MatchStatus.OnlyNegated:
            {
                string ruledOut = JoinLabels(result.Candidates);
                string body =
                    "You ruled out " + ruledOut + " but did not state an alternative diagnosis.\n\n"
                  + "The correct answer is " + CorrectType.Label + ". " + CorrectType.Explanation;
                string spoken =
                    "You ruled that out but did not give an alternative diagnosis. " + CorrectType.Spoken;
                ShowFeedback("Incomplete diagnosis", body, false);
                tts.SpeakOnDemand(spoken);
                break;
            }

            case MatchStatus.GenericOnly:
            {
                string body =
                    "You identified this as BPPV but did not specify which semicircular canal is involved.\n\n"
                  + "The correct answer is " + CorrectType.Label + ". " + CorrectType.Explanation;
                string spoken =
                    "That names BPPV generally but not a specific canal. " + CorrectType.Spoken;
                ShowFeedback("Incomplete diagnosis", body, false);
                tts.SpeakOnDemand(spoken);
                break;
            }

            case MatchStatus.NoMatch:
            default:
                ShowFeedback(FallbackTitle, FallbackBody, false);
                tts.SpeakOnDemand(FallbackSpoken);
                break;
        }
    }

    static string JoinLabels(List<BPPVType> types)
    {
        var labels = new List<string>();
        foreach (var t in types) labels.Add(t.Label);
        return string.Join(" and ", labels);
    }

    void ShowFeedback(string title, string body, bool correct)
    {
        feedbackTitleText.text = title;
        feedbackBodyText.text = body;
        feedbackTitleText.color = correct
            ? new Color(0.11f, 0.60f, 0.35f) // green
            : new Color(0.78f, 0.20f, 0.20f); // red
    }

    // ---------------------------------------------------------------------
    // Pure matching logic below. No UI, no TTS, no MonoBehaviour lifecycle,
    // so it is directly unit-testable from an EditMode test without a scene.
    // ---------------------------------------------------------------------

    public static EvaluationResult EvaluateTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new EvaluationResult { Status = MatchStatus.NoMatch };

        string[] words = Tokenize(text);

        var affirmed = new List<BPPVType>();
        var negatedOnly = new List<BPPVType>();

        foreach (var type in KnownTypes)
        {
            bool sawAffirmed = false;
            bool sawNegated = false;

            foreach (string kw in type.Keywords)
            {
                foreach (int idx in FindPhraseWordIndices(words, kw))
                {
                    if (IsNegatedBefore(words, idx))
                        sawNegated = true;
                    else
                        sawAffirmed = true;
                }
            }

            if (sawAffirmed) affirmed.Add(type);
            else if (sawNegated) negatedOnly.Add(type);
        }

        if (affirmed.Count == 1)
            return new EvaluationResult { Status = MatchStatus.SingleMatch, Matched = affirmed[0] };

        if (affirmed.Count > 1)
            return new EvaluationResult { Status = MatchStatus.MultipleMatches, Candidates = affirmed };

        if (negatedOnly.Count > 0)
            return new EvaluationResult { Status = MatchStatus.OnlyNegated, Candidates = negatedOnly };

        foreach (var phrase in GenericAcknowledgementPhrases)
        {
            if (FindPhraseWordIndices(words, string.Join(" ", phrase)).Count > 0)
                return new EvaluationResult { Status = MatchStatus.GenericOnly };
        }

        return new EvaluationResult { Status = MatchStatus.NoMatch };
    }

    static string[] Tokenize(string text)
    {
        // Turn sentence punctuation into its own boundary token so negation
        // scope can't leak across clauses once punctuation is stripped below.
        string withBoundaries = Regex.Replace(text, @"[,.;:!?]", " ¶ ");
        string cleaned = Regex.Replace(withBoundaries.ToLowerInvariant(), @"[^a-z0-9'¶\s]", " ");
        return cleaned.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
    }

    static List<int> FindPhraseWordIndices(string[] words, string phrase)
    {
        string[] phraseWords = phrase.ToLowerInvariant().Split(' ');
        var hits = new List<int>();
        for (int i = 0; i + phraseWords.Length <= words.Length; i++)
        {
            if (MatchesAt(words, i, phraseWords))
                hits.Add(i);
        }
        return hits;
    }

    static bool MatchesAt(string[] words, int start, string[] phrase)
    {
        for (int j = 0; j < phrase.Length; j++)
            if (words[start + j] != phrase[j]) return false;
        return true;
    }

    static bool IsNegatedBefore(string[] words, int keywordStartIndex)
    {
        int left = keywordStartIndex;
        int steps = 0;
        while (left > 0 && steps < NegationWindowWords)
        {
            string prev = words[left - 1];
            if (ClauseBoundaryTokens.Contains(prev))
                break;
            left--;
            steps++;
        }

        for (int i = left; i < keywordStartIndex; i++)
        {
            foreach (var cue in NegationCueSequences)
            {
                if (i + cue.Length <= keywordStartIndex && MatchesAt(words, i, cue))
                    return true;
            }
        }
        return false;
    }
}