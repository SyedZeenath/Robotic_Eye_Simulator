using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

public class RecordingIndicator : MonoBehaviour
{
    [Header("UI References")]
    public Image micIcon;
    public Image arcRight;
    public Image arcLeft;
    public TextMeshProUGUI statusText;

    [Header("Colors")]
    public Color recordingColor = new Color(0.86f, 0.20f, 0.20f, 1f);
    public Color processingColor = new Color(0.20f, 0.60f, 0.86f, 1f);

    [Header("Animation")]
    public float pulseSpeed = 1.2f;

    private bool _isRecording = false;
    private Coroutine _pulseCoroutine = null;

    void Start()
    {
        SetVisible(false);
    }

    public void StartRecording()
    {
        SetVisible(true);
        _isRecording = true;
        if (micIcon != null) micIcon.color = recordingColor;
        if (statusText != null) statusText.text = "Listening...";
        if (_pulseCoroutine != null) StopCoroutine(_pulseCoroutine);
        _pulseCoroutine = StartCoroutine(PulseArcs());
    }

    public void StopRecording()
    {
        _isRecording = false;
        if (micIcon != null) micIcon.gameObject.SetActive(false);
        if (statusText != null) statusText.text = "Processing...";
        if (_pulseCoroutine != null)
        {
            StopCoroutine(_pulseCoroutine);
            _pulseCoroutine = null;
        }
        ResetArcs();
        StartCoroutine(HideAfterDelay(1.2f));
    }

    IEnumerator PulseArcs()
    {
        while (_isRecording)
        {
            // Fade both arcs in together
            float t = 0f;
            while (t < 1f && _isRecording)
            {
                t += Time.deltaTime * pulseSpeed;
                SetAlpha(arcRight, Mathf.Lerp(0f, 1f, t));
                SetAlpha(arcLeft, Mathf.Lerp(0f, 1f, t));
                yield return null;
            }

            yield return new WaitForSeconds(0.3f);

            // Fade both arcs out together
            t = 0f;
            while (t < 1f && _isRecording)
            {
                t += Time.deltaTime * pulseSpeed;
                SetAlpha(arcRight, Mathf.Lerp(1f, 0f, t));
                SetAlpha(arcLeft, Mathf.Lerp(1f, 0f, t));
                yield return null;
            }

            yield return new WaitForSeconds(0.1f);
        }
    }

    IEnumerator HideAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        SetVisible(false);
    }

    void ResetArcs()
    {
        SetAlpha(arcRight, 0f);
        SetAlpha(arcLeft, 0f);
    }

    void SetAlpha(Image arc, float alpha)
    {
        if (arc == null) return;
        Color c = arc.color;
        c.a = alpha;
        arc.color = c;
    }

    void SetVisible(bool visible)
    {
        if (micIcon != null) micIcon.gameObject.SetActive(visible);
        if (arcRight != null) arcRight.gameObject.SetActive(visible);
        if (arcLeft != null) arcLeft.gameObject.SetActive(visible);
        if (statusText != null) statusText.gameObject.SetActive(visible);
    }
}