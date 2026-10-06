using System;
using UnityEngine;

namespace AmpmLib.Samples
{
	/// <summary>
	/// Rich-text helpers shared by the AMPM sample panels.
	/// </summary>
	internal static class AmpmSampleText
	{
		public static string Line(string label, string value)
		{
			return "<b>" + label + ":</b>  " + value;
		}

		public static string YesNo(bool value)
		{
			return value ? Green("Yes") : Red("No");
		}

		public static string Green(string text)
		{
			return "<color=#5ec46a>" + text + "</color>";
		}

		public static string Red(string text)
		{
			return "<color=#e0685c>" + text + "</color>";
		}

		public static string Yellow(string text)
		{
			return "<color=#ffcc4d>" + text + "</color>";
		}

		public static string Seconds(float seconds)
		{
			return seconds <= 0f ? "off" : seconds.ToString("0.#") + " s";
		}

		public static string Ago(DateTime time)
		{
			return (DateTime.Now - time).TotalSeconds.ToString("0") + " s ago";
		}
	}

	/// <summary>
	/// Layout helpers shared by the AMPM sample panels.
	/// </summary>
	internal static class AmpmSampleLayout
	{
		// A panel's root is a Canvas. On its own, Unity sizes it to the screen; nested under another
		// Canvas it keeps its own rect, so stretch it to fill the parent instead of drifting off-screen.
		public static void FillParent(Transform transform)
		{
			var rect = transform as RectTransform;
			if (rect == null)
				return;

			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.pivot = new Vector2(0.5f, 0.5f);
			rect.offsetMin = Vector2.zero;
			rect.offsetMax = Vector2.zero;
			rect.localScale = Vector3.one;
		}
	}
}
