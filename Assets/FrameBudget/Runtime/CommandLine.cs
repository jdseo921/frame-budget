using System;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// One place that reads the process command line, shared by the editor build script, the editor
    /// CLI entry point and the built player, so a flag means the same thing everywhere. Unknown
    /// arguments are ignored: Unity puts plenty of its own on the line.
    /// </summary>
    public static class CommandLine
    {
        public static bool HasFlag(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Value following <paramref name="name"/>, or <paramref name="fallback"/> when the flag is absent or last on the line.</summary>
        public static string GetString(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    string value = args[i + 1];
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                    Debug.LogWarning("[FrameBudget] " + name + " was given an empty value; using " + fallback + ".");
                    return fallback;
                }
            }
            return fallback;
        }
    }
}
