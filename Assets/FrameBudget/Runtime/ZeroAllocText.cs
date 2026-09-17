using System.Text;

namespace FrameBudget
{
    /// <summary>
    /// Number and padding helpers that append to a <see cref="StringBuilder"/> without allocating.
    ///
    /// StringBuilder is only half the answer. On the class library this project builds against,
    /// <c>Append(int)</c> and <c>Append(double)</c> format via <c>ToString()</c> internally and hand
    /// back a temporary string every call, so a builder full of numbers still produces garbage - just
    /// less of it than string concatenation. These write digits a character at a time instead, which
    /// is genuinely allocation-free.
    ///
    /// Used by the HUD when the zeroAlloc technique is on. The naive path stays as it was, because it
    /// is the control being measured against.
    /// </summary>
    public static class ZeroAllocText
    {
        private const int MaxDigits = 20;

        /// <summary>Appends a signed integer.</summary>
        public static void AppendInt(StringBuilder sb, long value)
        {
            if (value < 0)
            {
                sb.Append('-');
                value = -value;
            }
            AppendUnsigned(sb, (ulong)value);
        }

        private static void AppendUnsigned(StringBuilder sb, ulong value)
        {
            if (value == 0)
            {
                sb.Append('0');
                return;
            }

            // Digits come out least-significant first, so they are staged on the stack and reversed.
            // A stackalloc-free version keeps this usable without unsafe code.
            int start = sb.Length;
            while (value > 0)
            {
                sb.Append((char)('0' + (int)(value % 10)));
                value /= 10;
            }
            Reverse(sb, start, sb.Length - 1);
        }

        private static void Reverse(StringBuilder sb, int from, int to)
        {
            while (from < to)
            {
                char tmp = sb[from];
                sb[from] = sb[to];
                sb[to] = tmp;
                from++;
                to--;
            }
        }

        /// <summary>Appends a fixed-point number, rounded half away from zero, or "--" when it is not a number.</summary>
        public static void AppendFixed(StringBuilder sb, double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("--");
                return;
            }

            if (value < 0)
            {
                sb.Append('-');
                value = -value;
            }

            long scale = 1;
            for (int i = 0; i < decimals; i++) scale *= 10;

            // Round half away from zero, matching what ToString("F2") produces closely enough that
            // the HUD does not visibly flicker between the two paths.
            long scaled = (long)(value * scale + 0.5);
            AppendUnsigned(sb, (ulong)(scaled / scale));
            if (decimals <= 0) return;

            sb.Append('.');
            long fraction = scaled % scale;
            for (long divisor = scale / 10; divisor >= 1; divisor /= 10)
            {
                sb.Append((char)('0' + (int)(fraction / divisor % 10)));
            }
        }

        /// <summary>Appends <paramref name="text"/> then spaces, to at least <paramref name="width"/> characters.</summary>
        public static void AppendPadRight(StringBuilder sb, string text, int width)
        {
            sb.Append(text);
            for (int i = text.Length; i < width; i++) sb.Append(' ');
        }

        /// <summary>Appends spaces so that the next <paramref name="contentLength"/> characters end at <paramref name="width"/>.</summary>
        public static void AppendPadTo(StringBuilder sb, int contentLength, int width)
        {
            for (int i = contentLength; i < width; i++) sb.Append(' ');
        }

        /// <summary>Right-aligns whatever was appended after <paramref name="markStart"/> within <paramref name="width"/>, by inserting spaces before it.</summary>
        public static void RightAlignSince(StringBuilder sb, int markStart, int width)
        {
            int written = sb.Length - markStart;
            if (written >= width) return;
            // Insert(int, string, int) rather than the char overload, which this class library does
            // not have; the literal is interned, so no string is allocated here.
            sb.Insert(markStart, " ", width - written);
        }

        /// <summary>True when the builder already holds exactly this string, so no new string need be materialised.</summary>
        public static bool ContentEquals(StringBuilder sb, string other)
        {
            if (other == null || sb.Length != other.Length) return false;
            for (int i = 0; i < other.Length; i++)
            {
                if (sb[i] != other[i]) return false;
            }
            return true;
        }
    }
}
