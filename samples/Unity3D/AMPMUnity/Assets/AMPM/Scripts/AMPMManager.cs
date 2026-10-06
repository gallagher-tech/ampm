using UnityEngine;

namespace AmpmLib
{
	/// <summary>
	/// Connects the app to AMPM. Put the AMPM prefab in the first scene: it initializes AMPM,
	/// sends a heartbeat every frame, delivers AMPM's messages on the main thread and applies
	/// kiosk-friendly player settings. It persists across scene loads, and only one can exist.
	/// </summary>
	[DefaultExecutionOrder(-10000)] // Initialize AMPM before other scripts' Awake can call it.
	[DisallowMultipleComponent]
	public class AMPMManager : MonoBehaviour
	{
		[Header("Connection")]
		[SerializeField]
		private AmpmSettings connection = new AmpmSettings();

		[Header("Player Settings")]
		[Tooltip("Keep the app running when its window loses focus. Without this, Unity pauses, the heartbeat stops and AMPM restarts the app.")]
		[SerializeField]
		private bool enforceRunInBackground = true;

		[Tooltip("Switch the window to the Fullscreen Mode below when the app starts.")]
		[SerializeField]
		private bool enforceFullscreen = true;

		[Tooltip("Full Screen Window (borderless) is recommended for kiosks.")]
		[SerializeField]
		private FullScreenMode fullscreenMode = FullScreenMode.FullScreenWindow;

		[Header("Editor")]
		[Tooltip("Connect to AMPM in Play mode in the Editor. Off by default, so working in the Editor doesn't send heartbeats or request the config.")]
		[SerializeField]
		private bool runInEditor = false;

		private static AMPMManager _instance;

		private void Awake()
		{
			// Only one manager: loading a scene again (e.g. returning to a menu) would otherwise add another.
			if (_instance != null && _instance != this)
			{
				Debug.LogWarning("AMPM: an AMPMManager already exists, so the duplicate on '" + name + "' was removed.");
				enabled = false;
				Destroy(gameObject);
				return;
			}
			_instance = this;

			// DontDestroyOnLoad only works on root objects.
			if (transform.parent != null)
			{
				Debug.LogWarning("AMPM: '" + name + "' was moved to the root of the hierarchy so it can persist across scenes.");
				transform.SetParent(null);
			}
			DontDestroyOnLoad(gameObject);

			if (Application.isEditor && !runInEditor)
			{
				Debug.Log("AMPM: not connecting in the Editor. Turn on Run In Editor on the AMPM prefab to test with AMPM.");
				enabled = false;
				return;
			}

			ApplyPlayerSettings();
			AMPM.Initialize(connection);
		}

		// Runs every frame while enabled, so the heartbeat stops automatically when the component is disabled.
		private void Update()
		{
			AMPM.ProcessMessages();

			// One heartbeat per rendered frame: AMPM shows the app's FPS from the spacing between heartbeats.
			AMPM.Heart();
		}

		private void OnDestroy()
		{
			if (_instance == this)
				_instance = null;
		}

		/// <summary>
		/// Asks AMPM to restart the app. Public so a UI Button's On Click can call it.
		/// </summary>
		public void RequestRestart()
		{
			AMPM.Restart();
		}

		// Player-only settings: in the Editor they would change the Editor's behavior, not the build's.
		private void ApplyPlayerSettings()
		{
			if (Application.isEditor)
				return;

			if (enforceRunInBackground)
				Application.runInBackground = true;

			if (enforceFullscreen)
				Screen.fullScreenMode = fullscreenMode;
		}

		// Statics survive between play sessions when the domain reload is skipped.
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetInstance()
		{
			_instance = null;
		}
	}
}
