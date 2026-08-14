using UnityEngine;

/// <summary>
/// Attach to any active GameObject in the scene(A separate GameObject is created
/// but kept disabled. Enable in the MainScene's Inspector to see the stats). 
/// Renders the currently applied eye angles and a
/// frame/time counter directly on screen, large and high-contrast, so a
/// camera filming the monitor can read exact values off individual frames.
/// </summary>
public class SyncDebugOverlay : MonoBehaviour
{
    public RealisticEyeController eyeController; // assign in Inspector

    private GUIStyle style;

    void OnGUI()
    {
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 18;
            style.normal.textColor = Color.yellow;
            style.fontStyle = FontStyle.Bold;
        }

        float torsion = GetTorsion();
        float vertical = GetVertical();
        float horizontal = GetHorizontal();

        // FRAME/T+time removed - only needed for the video-frame-stepping
        // method, not the static-hold test. Eye position is held steady once
        // a C: command settles, so these three values just sit still on screen.
        string text =
            $"TORSION: {torsion,7:F2}\n" +
            $"VERTICAL: {vertical,7:F2}\n" +
            $"HORIZONTAL: {horizontal,7:F2}";

        // Small, high-contrast box sized to fit all 3 lines at this font size
        // without clipping.
        GUI.Box(new Rect(10, 10, 220, 90), GUIContent.none);
        GUI.Label(new Rect(18, 14, 205, 80), text, style);
    }

    // --- Replace these three with real reads from RealisticEyeController ---
    float GetTorsion() => eyeController != null ? eyeController.SmoothTorsion : 0f;
    float GetVertical() => eyeController != null ? eyeController.SmoothVertical : 0f;
    float GetHorizontal() => eyeController != null ? eyeController.SmoothHorizontal : 0f;
}
