using UnityEditor;
using UnityEditor.SceneManagement;

public static class PlaySceneCommand
{
    // Usage:
    // unity -projectPath "path" -executeMethod PlaySceneCommand.PlayScene -scenePath "Assets/Scenes/MyScene.unity"
    public static void PlayScene()
    {
        // Read optional command line arg -scenePath
        string[] args = System.Environment.GetCommandLineArgs();
        string scenePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-scenePath" && i + 1 < args.Length)
            {
                scenePath = args[i + 1];
                break;
            }
        }

        if (string.IsNullOrEmpty(scenePath))
        {
            // Fallback: use currently active scene or a default
            scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                scenePath = "Assets/Scenes/BaseScene.unity"; // change to your default
            }
        }

        // Open the requested scene
        var opened = EditorSceneManager.OpenScene(scenePath);
        if (!opened.IsValid())
        {
            UnityEngine.Debug.LogError($"Failed to open scene: {scenePath}");
            return;
        }

        // Enter Play Mode
        EditorApplication.isPlaying = true;
    }
}
