using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class TTS_Script : MonoBehaviour
{
    [SerializeField] AudioSource audioSource;

    // Paste your Google Cloud API key here
    private const string API_KEY = "AIzaSyCEnvM5QiPpXLCIKihB3gwWqNtfHiPBHxQ";
    private const string URL = "https://texttospeech.googleapis.com/v1/text:synthesize";

    public bool IsReady => true;
    public bool IsSpeaking { get; private set; } = false;

    public void SpeakOnDemand(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        // CRITICAL: Set IsSpeaking IMMEDIATELY before starting coroutine
        // This prevents race conditions where Arduino checks IsSpeaking before coroutine runs
        IsSpeaking = true;
        
        if (audioSource.isPlaying)
        {
            Debug.Log("[TTS] Stopping previous speech");
            StopAllCoroutines();
            audioSource.Stop();
        }
        
        StartCoroutine(SpeakCoroutine(text));
    }

    IEnumerator SpeakCoroutine(string text)
    {
        // IsSpeaking already set to true in SpeakOnDemand()
        
        // en-US-Neural2-F — natural female voice, non-robotic
        // Other good options: en-US-Neural2-C (female), en-US-Neural2-D (male)
        string json = $@"
        {{
            ""input"": {{ ""text"": ""{EscapeJson(text)}"" }},
            ""voice"": {{
                ""languageCode"": ""en-US"",
                ""name"": ""en-US-Neural2-F""
            }},
            ""audioConfig"": {{
                ""audioEncoding"": ""LINEAR16"",
                ""speakingRate"": 1.0
            }}
        }}";    

        byte[] body = Encoding.UTF8.GetBytes(json);

        using var req = new UnityWebRequest(URL + "?key=" + API_KEY, "POST");
        req.uploadHandler   = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            // Response JSON contains base64-encoded WAV in "audioContent" field
            string base64 = ExtractAudioContent(req.downloadHandler.text);

            if (!string.IsNullOrEmpty(base64))
            {
                byte[] wavBytes = Convert.FromBase64String(base64);
                AudioClip clip = WavUtility.ToAudioClip(wavBytes, 0, "tts");
                audioSource.clip = clip;
                audioSource.Play();
                
                Debug.Log($"[TTS] Playing audio - duration: {clip.length:F1}s");
                
                // Wait until audio finishes playing
                yield return new WaitWhile(() => audioSource.isPlaying);
            }
            else
            {
                Debug.LogError("TTS: audioContent missing in response.\n"
                               + req.downloadHandler.text);
            }
        }
        else
        {
            Debug.LogError("TTS request failed: " + req.error
                           + "\n" + req.downloadHandler.text);
        }

        IsSpeaking = false;
        Debug.Log("[TTS] Speech completed");
    }

    // Pulls the audioContent value out of the JSON response without a JSON library
    private string ExtractAudioContent(string json)
    {
        const string key = "\"audioContent\": \"";
        int start = json.IndexOf(key);
        if (start < 0) return null;
        start += key.Length;
        int end = json.IndexOf('"', start);
        return end < 0 ? null : json.Substring(start, end - start);
    }

    private string EscapeJson(string s)
        => s.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\n", " ")
             .Replace("\r", "");
}