// Minimal WAV decoder — converts raw WAV bytes to Unity AudioClip in memory
using System;
using UnityEngine;

public static class WavUtility
{
    public static AudioClip ToAudioClip(byte[] wavBytes, int offsetSamples = 0, string name = "tts")
    {
        // WAV header is 44 bytes for standard PCM
        // Parse sample rate and channel count from header
        int channels = wavBytes[22];
        int sampleRate = BitConverter.ToInt32(wavBytes, 24);
        int bitDepth = wavBytes[34];
        int headerSize = 44;

        // Find "data" chunk in case header is non-standard
        int dataIndex = FindDataChunk(wavBytes);
        if (dataIndex > 0) headerSize = dataIndex + 8;

        int dataSize = wavBytes.Length - headerSize;
        int sampleCount = dataSize / (bitDepth / 8) / channels;

        float[] samples = new float[sampleCount * channels];

        if (bitDepth == 16)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                int byteIndex = headerSize + i * 2;
                short s = BitConverter.ToInt16(wavBytes, byteIndex);
                samples[i] = s / 32768f;
            }
        }
        else if (bitDepth == 8)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (wavBytes[headerSize + i] - 128) / 128f;
        }

        AudioClip clip = AudioClip.Create(name, sampleCount, channels, sampleRate, false);
        clip.SetData(samples, offsetSamples);
        return clip;
    }

    private static int FindDataChunk(byte[] bytes)
    {
        for (int i = 12; i < bytes.Length - 4; i++)
        {
            if (bytes[i] == 'd' && bytes[i+1] == 'a' &&
                bytes[i+2] == 't' && bytes[i+3] == 'a')
                return i;
        }
        return -1;
    }
}