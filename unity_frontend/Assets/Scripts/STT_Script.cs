using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class STT_Script : MonoBehaviour
{
    string apiKey = "AIzaSyAzGaAC0sVkNAiJZo7H_znX1bW_-C5THoY"; // TODO: Move this to a secure location, not hardcoded in the script

    public string RecognizedText { get; private set; } = "";

    public IEnumerator Recognize(byte[] audioData, Action<string> onResult)
    {
        string url = $"https://speech.googleapis.com/v1/speech:recognize?key={apiKey}";

        // Convert WAV bytes to base64 — Google REST API expects base64 encoded audio
        // Strip the 44-byte WAV header, send only the raw PCM data
        byte[] pcmOnly = new byte[audioData.Length - 44];
        Array.Copy(audioData, 44, pcmOnly, 0, pcmOnly.Length);
        string base64Audio = Convert.ToBase64String(pcmOnly);

        string json = "{"
            + "\"config\":{"
            + "\"encoding\":\"LINEAR16\","
            + "\"sampleRateHertz\":16000,"
            + "\"languageCode\":\"en-GB\""
            + "},"
            + "\"audio\":{"
            + "\"content\":\"" + base64Audio + "\""
            + "}"
            + "}";

        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string response = request.downloadHandler.text;
            Debug.Log("[STT] Raw response: " + response);

            RecognizedText = ExtractTranscript(response).ToLower().Trim();
            Debug.Log("[STT] Recognized: " + RecognizedText);
            onResult?.Invoke(RecognizedText);
        }
        else
        {
            Debug.LogError("[STT] Error: " + request.error);
            Debug.LogError("[STT] Response body: " + request.downloadHandler.text);
            onResult?.Invoke("");
        }
    }

    // Extracts transcript from Google STT response JSON
    static string ExtractTranscript(string json)
    {
        // Handle both "transcript":"text" and "transcript": "text" (with space)
        string[] candidates = { "\"transcript\":\"", "\"transcript\": \"" };
        
        foreach (string key in candidates)
        {
            int start = json.IndexOf(key);
            if (start < 0) continue;
            start += key.Length;
            int end = json.IndexOf('"', start);
            if (end < 0) continue;
            return json.Substring(start, end - start);
        }
        return "";
    }
}