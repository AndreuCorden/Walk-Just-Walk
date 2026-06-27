using UnityEngine;

/// <summary>
/// Simple helper script to initialize secondary monitor outputs for local couch asymmetric testing.
/// </summary>
public class DisplayActivator : MonoBehaviour
{
    void Start()
    {
        Debug.Log($"Total displays detected: {Display.displays.Length}");
        
        // Display 0 is always active by default. Activate Display 1 if it exists.
        if (Display.displays.Length > 1)
        {
            Display.displays[1].Activate();
            Debug.Log("Display 2 Activated successfully.");
        }
    }
}