using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AmpmLib
{
	/// <summary>
	/// Shortens log messages that are too large to send to AMPM, removing the least useful
	/// information first. Each step builds on the previous ones, and trimming stops as soon as
	/// the message fits. The full message is always kept locally in Player.log (see the [ref] ID).
	/// </summary>
	internal static class AmpmLogTrimmer
	{
		// Stack frame lines in Unity's format ("Kiosk.ScreenManager:Show (string) (at Assets/Scripts/ScreenManager.cs:88)")
		// and .NET's ("  at Kiosk.ScreenManager.Show (System.String id) [0x00012] in C:\...\ScreenManager.cs:88").
		// Both require a method name directly followed by "(", so plain text such as
		// "at gallery entrance" is never mistaken for a frame.
		private static readonly Regex UnityFrame = new Regex(@"^(\s*)([^\s(:]+):([^\s(]+)\s*\([^()]*\)(.*)$");
		private static readonly Regex DotNetFrame = new Regex(@"^(\s*at\s+)([^\s(]+)\s*\([^()]*\)(.*)$");

		// Path and noise patterns removed in the clean-up step.
		private static readonly Regex UnityPath = new Regex(@"\(at\s+.*[\\/]([^\\/)]+:\d+)\)");
		private static readonly Regex DotNetPath = new Regex(@"\s+in\s+.*[\\/]([^\\/]+:\d+)\s*$");
		private static readonly Regex UnknownSource = new Regex(@"\s+in\s+<[0-9a-fA-F]+>:\d+\s*$");
		private static readonly Regex IlOffset = new Regex(@"\s*\[0x[0-9a-fA-F]+\]");

		private static readonly string[] FrameworkPrefixes = { "UnityEngine.", "UnityEditor.", "System.", "Mono." };

		private const string OmittedPrefix = "  ... ";
		private const string InnerExceptionEnd = "--- End of inner exception stack trace ---";

		// Frames kept at the top and bottom of a long run when the middle is removed.
		private const int KeepTopFrames = 3;
		private const int KeepBottomFrames = 1;

		/// <summary>
		/// Returns the message, trimmed until <paramref name="fits"/> accepts it.
		/// </summary>
		public static string Trim(string message, Func<string, bool> fits)
		{
			if (message == null)
				return null;

			var steps = new Func<List<string>, List<string>>[]
			{
				CleanUp,                 // 1. shorten paths, collapse blank lines, merge repeated frames
				DropFrameworkFrames,     // 2. remove Unity / .NET frames
				DropMiddleFrames,        // 3. keep the top and bottom of long runs of frames
				ShortenFrames,           // 4. drop namespaces and parameter lists
				DropInnerExceptionTraces // 5. keep inner exceptions' type and message only
			};

			List<string> lines = new List<string>(message.Replace("\r\n", "\n").Split('\n'));
			foreach (var step in steps)
			{
				lines = step(lines);
				string candidate = string.Join("\n", lines);
				if (fits(candidate))
					return candidate;
			}

			// 6. Last resort: cut the middle of the message, keeping its start and end.
			return CutMiddle(string.Join("\n", lines), fits);
		}

		private static bool IsFrame(string line)
		{
			return UnityFrame.IsMatch(line) || DotNetFrame.IsMatch(line);
		}

		private static bool IsOmittedMarker(string line)
		{
			return line.StartsWith(OmittedPrefix, StringComparison.Ordinal);
		}

		// The fully qualified method of a frame line, e.g. "UnityEngine.Debug:LogError" or "System.Threading.Thread.Start".
		private static string FrameMethod(string line)
		{
			Match unity = UnityFrame.Match(line);
			if (unity.Success)
				return unity.Groups[2].Value + ":" + unity.Groups[3].Value;

			Match dotNet = DotNetFrame.Match(line);
			return dotNet.Success ? dotNet.Groups[2].Value : null;
		}

		private static List<string> CleanUp(List<string> lines)
		{
			var result = new List<string>();
			string previous = null;
			int repeats = 0;

			foreach (string raw in lines)
			{
				string line = raw.TrimEnd();
				line = UnknownSource.Replace(line, "");
				line = IlOffset.Replace(line, "");
				line = UnityPath.Replace(line, "(at $1)");
				line = DotNetPath.Replace(line, " in $1");

				// Collapse runs of blank lines into one.
				if (line.Length == 0 && previous != null && previous.Length == 0)
					continue;

				// Merge identical consecutive frames (e.g. recursion) into one line with a count.
				if (previous != null && line == previous && IsFrame(line))
				{
					repeats++;
					continue;
				}

				if (repeats > 0)
				{
					result[result.Count - 1] += " (x" + (repeats + 1) + ")";
					repeats = 0;
				}

				result.Add(line);
				previous = line;
			}

			if (repeats > 0)
				result[result.Count - 1] += " (x" + (repeats + 1) + ")";

			return result;
		}

		private static List<string> DropFrameworkFrames(List<string> lines)
		{
			var result = new List<string>();
			int dropped = 0;

			foreach (string line in lines)
			{
				string method = IsFrame(line) ? FrameMethod(line) : null;
				if (method != null && IsFrameworkMethod(method))
				{
					dropped++;
					continue;
				}

				if (dropped > 0)
				{
					result.Add(OmittedPrefix + Plural(dropped, "framework frame") + " omitted");
					dropped = 0;
				}
				result.Add(line);
			}

			if (dropped > 0)
				result.Add(OmittedPrefix + Plural(dropped, "framework frame") + " omitted");

			return result;
		}

		private static string Plural(int count, string noun)
		{
			return count + " " + noun + (count == 1 ? "" : "s");
		}

		private static bool IsFrameworkMethod(string method)
		{
			foreach (string prefix in FrameworkPrefixes)
			{
				if (method.StartsWith(prefix, StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		private static List<string> DropMiddleFrames(List<string> lines)
		{
			var result = new List<string>();
			int i = 0;

			while (i < lines.Count)
			{
				if (!IsFrame(lines[i]) && !IsOmittedMarker(lines[i]))
				{
					result.Add(lines[i]);
					i++;
					continue;
				}

				// Collect a run of frames (and earlier "omitted" markers).
				int start = i;
				while (i < lines.Count && (IsFrame(lines[i]) || IsOmittedMarker(lines[i])))
					i++;
				int count = i - start;

				// The bottom of the run starts at its last real frames, so a trailing "omitted"
				// marker never takes the place of the entry-point frame.
				int bottomStart = i;
				int bottomFrames = 0;
				while (bottomStart > start && bottomFrames < KeepBottomFrames)
				{
					bottomStart--;
					if (IsFrame(lines[bottomStart]))
						bottomFrames++;
				}

				int topEnd = start + KeepTopFrames;
				if (count <= KeepTopFrames + KeepBottomFrames + 1 || topEnd >= bottomStart)
				{
					result.AddRange(lines.GetRange(start, count));
					continue;
				}

				int omitted = 0;
				for (int j = topEnd; j < bottomStart; j++)
				{
					if (IsFrame(lines[j]))
						omitted++;
				}

				result.AddRange(lines.GetRange(start, KeepTopFrames));
				result.Add(OmittedPrefix + Plural(omitted, "frame") + " omitted ...");
				result.AddRange(lines.GetRange(bottomStart, i - bottomStart));
			}

			return result;
		}

		private static List<string> ShortenFrames(List<string> lines)
		{
			var result = new List<string>(lines.Count);

			foreach (string line in lines)
			{
				Match unity = UnityFrame.Match(line);
				if (unity.Success)
				{
					// "Kiosk.ScreenManager:Show (string) (at ScreenManager.cs:88)" -> "ScreenManager:Show () (at ScreenManager.cs:88)"
					result.Add(unity.Groups[1].Value + LastSegments(unity.Groups[2].Value, 1) + ":" + unity.Groups[3].Value + " ()" + unity.Groups[4].Value);
					continue;
				}

				Match dotNet = DotNetFrame.Match(line);
				if (dotNet.Success)
				{
					// "at Kiosk.ScreenManager.Show (System.String id) in ScreenManager.cs:88" -> "at ScreenManager.Show () in ScreenManager.cs:88"
					result.Add(dotNet.Groups[1].Value + LastSegments(dotNet.Groups[2].Value, 2) + " ()" + dotNet.Groups[3].Value);
					continue;
				}

				result.Add(line);
			}

			return result;
		}

		// The last <count> dot-separated segments of a qualified name, e.g. ("Kiosk.ScreenManager.Show", 2) -> "ScreenManager.Show".
		private static string LastSegments(string name, int count)
		{
			string[] parts = name.Split('.');
			if (parts.Length <= count)
				return name;
			return string.Join(".", parts, parts.Length - count, count);
		}

		private static List<string> DropInnerExceptionTraces(List<string> lines)
		{
			var result = new List<string>();

			foreach (string line in lines)
			{
				if (line.Trim() == InnerExceptionEnd)
				{
					// The frames just before this marker belong to the inner exception; keep only its
					// "---> Type: message" line, which appears earlier in the message.
					while (result.Count > 0 && (IsFrame(result[result.Count - 1]) || IsOmittedMarker(result[result.Count - 1])))
						result.RemoveAt(result.Count - 1);
					continue;
				}

				result.Add(line);
			}

			return result;
		}

		private static string CutMiddle(string text, Func<string, bool> fits)
		{
			// Binary search for the longest kept length (start + end) that fits.
			int low = 0;
			int high = text.Length;
			string best = BuildCut(text, 0);

			while (low <= high)
			{
				int keep = (low + high) / 2;
				string candidate = BuildCut(text, keep);
				if (fits(candidate))
				{
					best = candidate;
					low = keep + 1;
				}
				else
				{
					high = keep - 1;
				}
			}

			return best;
		}

		// Keeps <keep> characters: two thirds from the start (which holds the [ref] ID and the
		// message) and one third from the end.
		private static string BuildCut(string text, int keep)
		{
			if (keep >= text.Length)
				return text;

			int head = keep * 2 / 3;
			int tail = keep - head;
			return text.Substring(0, head)
				+ "\n...[truncated " + (text.Length - keep) + " characters]...\n"
				+ text.Substring(text.Length - tail);
		}
	}
}
