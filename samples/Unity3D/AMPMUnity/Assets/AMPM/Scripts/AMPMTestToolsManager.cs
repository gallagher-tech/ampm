using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.Diagnostics;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace AmpmLib
{
	/// <summary>
	/// A stand-in for AMPM running inside the app, like the MockAMPM sample. While one is registered
	/// (<see cref="AMPMTestToolsManager.StandIn"/>), Crash and Freeze Forever also work in the Editor:
	/// the stand-in reacts the way AMPM would, instead of Unity crashing or hanging for good.
	/// </summary>
	public interface IAmpmStandIn
	{
		/// <summary>Editor: the app "crashed" (Unity keeps running). React like AMPM to the process exiting.</summary>
		void SimulateCrash();

		/// <summary>Build: the app is about to crash for real. Arrange whatever AMPM would do afterwards.</summary>
		void PrepareForCrash();
	}

	/// <summary>
	/// Optional test tool to check an app's AMPM setup: put the AMPMTestToolsManager prefab in the first
	/// scene next to AMPMManager. A button in the centre of the screen opens a panel to ask AMPM to
	/// restart the app, crash it (tests restartOnProcessExit) or freeze it forever (tests
	/// heartbeatTimeout), after a countdown that can be cancelled. Only shown in development builds
	/// unless allowed in release builds.
	/// </summary>
	[DisallowMultipleComponent]
	public class AMPMTestToolsManager : MonoBehaviour, IBeginDragHandler, IDragHandler
	{
		[Header("Safety")]
		[Tooltip("Show the test button in release builds too. Off: it only appears in development builds and the Editor, so it can't be left in a live install by accident.")]
		[SerializeField]
		private bool allowInReleaseBuilds = false;

		[Header("Countdown")]
		[Tooltip("Seconds between choosing a test and it happening. Cancel stops it.")]
		[Range(0f, 30f)]
		[SerializeField]
		private float countdownSeconds = 3f;

		[Header("Layout")]
		[Tooltip("The app runs in portrait: lay the test UI out for 1080 x 1920 instead of 1920 x 1080. It scales to the actual screen either way (e.g. 2x on 4K).")]
		[SerializeField]
		private bool isPortraitOrientation = false;

		[Header("UI")]
		[SerializeField] private GameObject openButton;
		[SerializeField] private GameObject menuPanel;
		[SerializeField] private GameObject countdownPanel;
		[SerializeField] private Text countdownText;

		private enum Action { None, Restart, Crash, FreezeForever }

		private const string Prefix = "[AMPM Test] ";

		// In the Editor, Freeze Forever ends on its own after this long, so Unity can never lock up for good.
		private const float EditorFreezeSafetySeconds = 60f;

		private static AMPMTestToolsManager _instance;

		// Set by the stand-in (from any thread) to end a freeze so it can restart the app.
		private static volatile bool _endFreeze;

		// Set at the same time and never cleared during this run: a restart is on its way, so further
		// tests are ignored. Otherwise a click queued while the app was frozen could freeze it again.
		private static volatile bool _restartPending;

		/// <summary>
		/// The stand-in for AMPM in this app, if any (the MockAMPM sample registers itself). Null with real AMPM.
		/// </summary>
		public static IAmpmStandIn StandIn { get; set; }

		/// <summary>
		/// Ends a running Freeze Forever. The stand-in calls this, from any thread, when it restarts the app.
		/// </summary>
		public static void EndFreeze()
		{
			_restartPending = true;
			_endFreeze = true;
		}

		private Action _pending;
		private float _actAt;

		// The window (open button, menu or countdown) being dragged.
		private RectTransform _dragged;

		private void Awake()
		{
			// Only one: loading a scene again would otherwise add another.
			if (_instance != null && _instance != this)
			{
				Destroy(gameObject);
				return;
			}
			_instance = this;

			if (transform.parent != null)
				transform.SetParent(null);
			DontDestroyOnLoad(gameObject);

			ApplyOrientation();

			if (!Debug.isDebugBuild && !allowInReleaseBuilds)
			{
				// Release build: stay out of the way entirely.
				gameObject.SetActive(false);
				return;
			}

			ShowClosed();
		}

		private void Start()
		{
			if (FindAnyObjectByType<AMPMManager>() == null)
				Debug.LogWarning(Prefix + "There's no AMPMManager in the scene, so the app isn't sending heartbeats to AMPM.");

			// The buttons need an EventSystem. Add one if the project doesn't have one.
			if (EventSystem.current == null && FindAnyObjectByType<EventSystem>() == null)
			{
#if ENABLE_LEGACY_INPUT_MANAGER
				var eventSystem = new GameObject("EventSystem (added by AMPMTestToolsManager)", typeof(EventSystem), typeof(StandaloneInputModule));
				DontDestroyOnLoad(eventSystem);
#else
				Debug.LogWarning(Prefix + "There's no EventSystem in the scene, so the AMPM test button can't be clicked. Add one (GameObject > UI > Event System).");
#endif
			}
		}

		private void Update()
		{
			if (_pending == Action.None)
				return;

			float remaining = _actAt - Time.unscaledTime;
			if (remaining > 0f)
			{
				countdownText.text = Describe(_pending) + " in " + Mathf.CeilToInt(remaining) + "...";
				return;
			}

			Action action = _pending;
			_pending = Action.None;
			if (IgnoreWhileRestarting(action))
			{
				ShowClosed();
				return;
			}

			if (action == Action.Restart)
				RestartNow();
			else if (action == Action.Crash)
				CrashNow();
			else
				FreezeForeverNow();
		}

		private void OnDestroy()
		{
			if (_instance == this)
				_instance = null;
		}

		// ---- Called by the prefab's buttons ----

		public void OpenMenu()
		{
			openButton.SetActive(false);
			menuPanel.SetActive(true);
			countdownPanel.SetActive(false);
		}

		public void CloseMenu()
		{
			ShowClosed();
		}

		public void Restart()
		{
			StartCountdown(Action.Restart, "Asking AMPM to restart the app in " + countdownSeconds + " s. AMPM should close it and run its launchCommand again.");
		}

		public void Crash()
		{
			StartCountdown(Action.Crash, "Crashing the app in " + countdownSeconds + " s to test AMPM. AMPM should relaunch it if restartOnProcessExit is true in ampm.json.");
		}

		public void FreezeForever()
		{
			StartCountdown(Action.FreezeForever, "Freezing the app forever in " + countdownSeconds + " s to test AMPM. AMPM should restart it once heartbeatTimeout in ampm.json passes.");
		}

		public void CancelCountdown()
		{
			if (_pending != Action.None)
				Debug.Log(Prefix + Name(_pending) + " cancelled.");
			_pending = Action.None;
			ShowClosed();
		}

		// ---- Dragging ----
		// Buttons don't handle drags, so a drag that starts on any window (or a button in it) reaches
		// this component on the root. A drag never counts as a click.

		public void OnBeginDrag(PointerEventData eventData)
		{
			_dragged = WindowUnder(eventData.pointerPressRaycast.gameObject);
			if (_dragged != null)
				_dragged.SetAsLastSibling(); // In front of the other windows while it moves.
		}

		public void OnDrag(PointerEventData eventData)
		{
			if (_dragged == null)
				return;

			var canvas = GetComponent<Canvas>();
			_dragged.anchoredPosition += eventData.delta / (canvas != null ? canvas.scaleFactor : 1f);
			KeepOnScreen(_dragged);
		}

		// The direct child of this canvas (one of the three windows) that contains the object.
		private RectTransform WindowUnder(GameObject hit)
		{
			Transform current = hit != null ? hit.transform : null;
			while (current != null && current.parent != transform)
				current = current.parent;
			return current as RectTransform;
		}

		private void KeepOnScreen(RectTransform window)
		{
			Rect bounds = ((RectTransform)transform).rect;
			var corners = new Vector3[4];
			window.GetLocalCorners(corners);

			Vector2 position = window.localPosition;
			float left = position.x + corners[0].x, right = position.x + corners[2].x;
			float bottom = position.y + corners[0].y, top = position.y + corners[2].y;

			Vector2 offset = Vector2.zero;
			if (left < bounds.xMin) offset.x = bounds.xMin - left;
			else if (right > bounds.xMax) offset.x = bounds.xMax - right;
			if (bottom < bounds.yMin) offset.y = bounds.yMin - bottom;
			else if (top > bounds.yMax) offset.y = bounds.yMax - top;

			window.anchoredPosition += offset;
		}

		// ---- Actions ----

		private void StartCountdown(Action action, string message)
		{
			if (IgnoreWhileRestarting(action))
				return;
			Debug.Log(Prefix + message);
			_pending = action;
			_actAt = Time.unscaledTime + countdownSeconds;
			menuPanel.SetActive(false);
			countdownPanel.SetActive(true);
		}

		private void RestartNow()
		{
			Debug.Log(Prefix + "Asking AMPM to restart the app now.");
			AMPM.Restart();
			ShowClosed();
		}

		private void CrashNow()
		{
			if (Application.isEditor)
			{
				if (StandIn == null)
				{
					Debug.Log(Prefix + "Crash skipped in the Editor (it would crash Unity). Test it in a development build launched by AMPM, or with the MockAMPM sample.");
				}
				else
				{
					Debug.Log(Prefix + "Simulating a crash (Unity itself isn't crashed).");
					StandIn.SimulateCrash();
				}
				ShowClosed();
				return;
			}

			Debug.Log(Prefix + "Crashing now.");
			if (StandIn != null)
				StandIn.PrepareForCrash();
			Thread.Sleep(200); // Give the log line time to reach Player.log.
			Utils.ForceCrash(ForcedCrashCategory.AccessViolation);
		}

		private void FreezeForeverNow()
		{
			bool editor = Application.isEditor;
			if (editor && StandIn == null)
			{
				Debug.Log(Prefix + "Freeze Forever skipped in the Editor (it would hang Unity). Test it in a development build launched by AMPM, or with the MockAMPM sample.");
				ShowClosed();
				return;
			}

			Debug.Log(Prefix + "Freezing now. Only AMPM can end this, by restarting the app.");
			_endFreeze = false;
			Stopwatch watch = Stopwatch.StartNew();
			while (!_endFreeze)
			{
				if (editor && watch.Elapsed.TotalSeconds >= EditorFreezeSafetySeconds)
				{
					Debug.LogWarning(Prefix + "Freeze Forever ended after " + EditorFreezeSafetySeconds + " s because nothing restarted the app. In the Editor it never lasts longer.");
					break;
				}
				Thread.Sleep(10);
			}

			if (_endFreeze)
				Debug.Log(Prefix + "AMPM's stand-in ended the freeze so it can restart the app.");
			_endFreeze = false;
			ShowClosed();
		}

		private static bool IgnoreWhileRestarting(Action action)
		{
			if (!_restartPending)
				return false;
			Debug.Log(Prefix + Name(action) + " ignored: the app is already being restarted.");
			return true;
		}

		private static string Name(Action action)
		{
			return action == Action.Restart ? "Restart" : action == Action.Crash ? "Crash" : "Freeze Forever";
		}

		private static string Describe(Action action)
		{
			return action == Action.Restart ? "Asking AMPM to restart" : action == Action.Crash ? "Crashing" : "Freezing forever";
		}

		// Keeps the prefab's preview in the Editor in step with the checkbox.
		private void OnValidate()
		{
			ApplyOrientation();
		}

		private void ApplyOrientation()
		{
			var scaler = GetComponent<CanvasScaler>();
			if (scaler != null)
				scaler.referenceResolution = isPortraitOrientation ? new Vector2(1080f, 1920f) : new Vector2(1920f, 1080f);
		}

		private void ShowClosed()
		{
			openButton.SetActive(true);
			menuPanel.SetActive(false);
			countdownPanel.SetActive(false);
		}

		// Statics survive between play sessions when the domain reload is skipped.
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetStatics()
		{
			_instance = null;
			_endFreeze = false;
			_restartPending = false;
		}
	}
}
