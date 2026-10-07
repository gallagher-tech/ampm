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

		[Header("Appearance")]
		[Tooltip("Dark: near-black panels and buttons with white text. Off (light): white with black text. Both have a red border.")]
		[SerializeField]
		private bool isDarkMode = false;

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

		// Sizing, "readable first": the UI grows with the screen as the prefab's Canvas Scaler would
		// (2x on 4K) but never shrinks below its design size, so its text stays legible in a small
		// window (e.g. a preview window), unless it has to shrink to fit the window at all.
		private const float ScreenMargin = 16f; // Screen pixels kept free around the largest window.
		private CanvasScaler _scaler;
		private Vector2 _referenceResolution;
		private float _match;
		private Vector2 _largestWindow; // The largest of the three windows, in design pixels.
		private Vector2Int _scaledFor;
		private bool _keepWindowsOnScreen;

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

			if (!Debug.isDebugBuild && !allowInReleaseBuilds)
			{
				// Release build: stay out of the way entirely.
				gameObject.SetActive(false);
				return;
			}

			ApplyTheme();
			SetUpScaling();
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
			if (_keepWindowsOnScreen)
			{
				// A frame after a scale change, once the canvas has its new size.
				_keepWindowsOnScreen = false;
				foreach (GameObject window in new[] { openButton, menuPanel, countdownPanel })
					KeepOnScreen((RectTransform)window.transform);
			}

			if (_scaler != null && (Screen.width != _scaledFor.x || Screen.height != _scaledFor.y))
				UpdateScale();

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

		// ---- Appearance ----

		private static readonly Color Border = new Color(0.9f, 0.2f, 0.14f);
		private const float BorderWidth = 3f;

		// Applies light or dark mode to every panel, button and text in the prefab. Each Image is a
		// panel or a button (with an Outline as its red border); Texts named "...Subtitle" or
		// "...Explanation" are secondary text, every other Text is primary.
		private void ApplyTheme()
		{
			Color panelFill = isDarkMode ? new Color(0.1f, 0.1f, 0.1f) : Color.white;
			Color buttonFill = isDarkMode ? new Color32(0x40, 0x40, 0x40, 0xFF) : new Color32(0xBF, 0xBF, 0xBF, 0xFF);
			Color primary = isDarkMode ? Color.white : Color.black;
			Color secondary = isDarkMode ? new Color(0.75f, 0.75f, 0.75f) : new Color(0.3f, 0.3f, 0.3f);

			foreach (Image image in GetComponentsInChildren<Image>(true))
			{
				var button = image.GetComponent<Button>();
				SetColor(image, button != null ? buttonFill : panelFill);

				var outline = image.GetComponent<Outline>();
				if (outline != null && (outline.effectColor != Border || outline.effectDistance != new Vector2(BorderWidth, -BorderWidth)))
				{
					outline.effectColor = Border;
					outline.effectDistance = new Vector2(BorderWidth, -BorderWidth);
					MarkChanged(outline);
				}

				if (button != null)
				{
					// The tint multiplies the fill (and its border): hover and press darken the gray.
					ColorBlock colors = button.colors;
					colors.normalColor = colors.selectedColor = Color.white;
					colors.highlightedColor = Gray(0.85f);
					colors.pressedColor = Gray(0.7f);
					colors.disabledColor = new Color(1f, 1f, 1f, 0.5f);
					colors.colorMultiplier = 1f;
					if (colors != button.colors)
					{
						button.colors = colors;
						MarkChanged(button);
					}
				}
			}

			foreach (Text text in GetComponentsInChildren<Text>(true))
			{
				bool isSecondary = text.name.EndsWith("Subtitle") || text.name.EndsWith("Explanation");
				SetColor(text, isSecondary ? secondary : primary);
			}
		}

		private static Color Gray(float value)
		{
			return new Color(value, value, value, 1f);
		}

		private static void SetColor(Graphic graphic, Color color)
		{
			if (graphic.color == color)
				return;
			graphic.color = color;
			MarkChanged(graphic);
		}

		// Changes made in the Editor (from OnValidate) are saved with the prefab or scene.
		private static void MarkChanged(Object changed)
		{
#if UNITY_EDITOR
			if (!Application.isPlaying)
				UnityEditor.EditorUtility.SetDirty(changed);
#endif
		}

#if UNITY_EDITOR
		// Shows the chosen mode in the Editor straight away. Deferred: UI components shouldn't be
		// changed from inside OnValidate itself.
		private void OnValidate()
		{
			UnityEditor.EditorApplication.delayCall += () =>
			{
				if (this != null && !Application.isPlaying)
					ApplyTheme();
			};
		}
#endif

		// ---- Sizing ----

		private void SetUpScaling()
		{
			_scaler = GetComponent<CanvasScaler>();
			if (_scaler == null)
				return;

			// The prefab's Canvas Scaler (Scale With Screen Size) describes the design; from here on
			// the scale is set directly.
			_referenceResolution = _scaler.referenceResolution;
			_match = _scaler.matchWidthOrHeight;
			_scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

			// Measure each window at its laid-out size (the menu and countdown size to their content).
			_largestWindow = Vector2.zero;
			foreach (GameObject window in new[] { openButton, menuPanel, countdownPanel })
			{
				bool wasActive = window.activeSelf;
				window.SetActive(true);
				var rect = (RectTransform)window.transform;
				LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
				_largestWindow = Vector2.Max(_largestWindow, rect.rect.size);
				window.SetActive(wasActive);
			}

			UpdateScale();
		}

		private void UpdateScale()
		{
			int width = Screen.width;
			int height = Screen.height;
			_scaledFor = new Vector2Int(width, height);
			if (width <= 0 || height <= 0)
				return;

			// What Scale With Screen Size would give (1x on 1080p, 2x on 4K).
			float logWidth = Mathf.Log(width / _referenceResolution.x, 2f);
			float logHeight = Mathf.Log(height / _referenceResolution.y, 2f);
			float screenScale = Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, _match));

			// The most the largest window can be scaled and still fit on screen.
			float fit = float.MaxValue;
			if (_largestWindow.x > 0f && _largestWindow.y > 0f)
				fit = Mathf.Min((width - 2f * ScreenMargin) / _largestWindow.x, (height - 2f * ScreenMargin) / _largestWindow.y);

			// Grow with the screen, never below design size, never bigger than fits.
			_scaler.scaleFactor = Mathf.Max(0.1f, Mathf.Min(Mathf.Max(screenScale, 1f), fit));
			_keepWindowsOnScreen = true;
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
