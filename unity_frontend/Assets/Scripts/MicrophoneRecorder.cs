using System;
using System.Collections;
using UnityEngine;

public class MicrophoneRecorder : MonoBehaviour
{
    [Header("Recording")]
    public int recordingSeconds = 8;
    public int sampleRate = 16000;

    public bool IsRecording { get; private set; }

    // Called when recording finishes; passes raw WAV bytes
    public event Action<byte[]> OnRecordingComplete;

    public void StartRecording()
    {
        if (IsRecording) return;
        StartCoroutine(RecordCoroutine());
    }

    IEnumerator RecordCoroutine()
    {
        IsRecording = true;
        Debug.Log("[MicrophoneRecorder] Recording started");

        AudioClip clip = Microphone.Start(null, false, recordingSeconds, sampleRate);

        // Wait until the microphone is actually running before starting the timer
        while (Microphone.GetPosition(null) <= 0)
            yield return null;

        yield return new WaitForSeconds(recordingSeconds);

        Microphone.End(null);
        IsRecording = false;

        byte[] wav = EncodeToWav(clip);
        Debug.Log($"[MicrophoneRecorder] Done. WAV size: {wav.Length} bytes");
        OnRecordingComplete?.Invoke(wav);
    }

    // Encode AudioClip PCM to a 16-bit mono WAV byte array
    static byte[] EncodeToWav(AudioClip clip)
    {
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        // Down-mix to mono if stereo
        int monoLength = clip.samples;
        short[] monoSamples = new short[monoLength];
        int channels = clip.channels;

        for (int i = 0; i < monoLength; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += samples[i * channels + c];
            monoSamples[i] = (short)Mathf.Clamp(sum / channels * 32767f, -32768f, 32767f);
        }

        int dataSize = monoLength * 2;
        int headerSize = 44;
        byte[] wav = new byte[headerSize + dataSize];

        int hz = clip.frequency;
        WriteWavHeader(wav, hz, dataSize);

        // Write PCM samples
        int offset = headerSize;
        foreach (short s in monoSamples)
        {
            wav[offset++] = (byte)(s & 0xFF);
            wav[offset++] = (byte)((s >> 8) & 0xFF);
        }

        return wav;
    }

    static void WriteWavHeader(byte[] buffer, int sampleRate, int dataSize)
    {
        // RIFF header
        buffer[0] = (byte)'R'; buffer[1] = (byte)'I';
        buffer[2] = (byte)'F'; buffer[3] = (byte)'F';
        WriteInt32(buffer, 4, 36 + dataSize);
        buffer[8] = (byte)'W'; buffer[9] = (byte)'A';
        buffer[10]= (byte)'V'; buffer[11] = (byte)'E';
        // fmt chunk
        buffer[12]= (byte)'f'; buffer[13] = (byte)'m';
        buffer[14]= (byte)'t'; buffer[15] = (byte)' ';
        WriteInt32(buffer, 16, 16); // chunk size
        WriteInt16(buffer, 20, 1); // PCM = 1
        WriteInt16(buffer, 22, 1); // mono
        WriteInt32(buffer, 24, sampleRate);
        WriteInt32(buffer, 28, sampleRate * 2); // byte rate
        WriteInt16(buffer, 32, 2); // block align
        WriteInt16(buffer, 34, 16); // bits per sample
        // data chunk
        buffer[36]= (byte)'d'; buffer[37] = (byte)'a';
        buffer[38]= (byte)'t'; buffer[39] = (byte)'a';
        WriteInt32(buffer, 40, dataSize);
    }

    static void WriteInt32(byte[] b, int offset, int val)
    {
        b[offset] = (byte)(val & 0xFF);
        b[offset+1] = (byte)((val >> 8) & 0xFF);
        b[offset+2] = (byte)((val >> 16) & 0xFF);
        b[offset+3] = (byte)((val >> 24) & 0xFF);
    }

    static void WriteInt16(byte[] b, int offset, short val)
    {
        b[offset] = (byte)(val & 0xFF);
        b[offset+1] = (byte)((val >> 8) & 0xFF);
    }
}