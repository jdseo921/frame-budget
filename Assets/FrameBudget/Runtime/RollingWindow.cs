using System;

namespace FrameBudget
{
    /// <summary>
    /// Fixed-capacity ring buffer of samples with median and 95th percentile on demand.
    /// Percentiles use the nearest-rank method on a sorted copy (index ceil(p*n)-1), so a reported
    /// value is always an actual sample, never an average. Means are not offered: a mean hides the
    /// spikes this instrument exists to show. All buffers are preallocated; Add and Percentile do
    /// not allocate.
    /// </summary>
    public sealed class RollingWindow
    {
        private readonly double[] ring;
        private readonly double[] scratch;
        private int head;   // index of the next write
        private int count;

        public RollingWindow(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            ring = new double[capacity];
            scratch = new double[capacity];
        }

        public int Capacity => ring.Length;
        public int Count => count;

        /// <summary>Oldest-to-newest access: index 0 is the oldest sample in the window.</summary>
        public double this[int index]
        {
            get
            {
                if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
                int start = (head - count + ring.Length) % ring.Length;
                return ring[(start + index) % ring.Length];
            }
        }

        public double Latest => count == 0 ? double.NaN : ring[(head - 1 + ring.Length) % ring.Length];

        public void Add(double value)
        {
            ring[head] = value;
            head = (head + 1) % ring.Length;
            if (count < ring.Length) count++;
        }

        public void Clear()
        {
            head = 0;
            count = 0;
        }

        public double Median => Percentile(0.5);
        public double P95 => Percentile(0.95);

        public double Max
        {
            get
            {
                if (count == 0) return double.NaN;
                double max = double.NegativeInfinity;
                for (int i = 0; i < count; i++) if (this[i] > max) max = this[i];
                return max;
            }
        }

        public double Percentile(double p)
        {
            if (count == 0) return double.NaN;
            int start = (head - count + ring.Length) % ring.Length;
            for (int i = 0; i < count; i++) scratch[i] = ring[(start + i) % ring.Length];
            Array.Sort(scratch, 0, count);
            return Percentiles.NearestRank(scratch, count, p);
        }
    }

    /// <summary>Shared percentile definition so the HUD and the benchmark CSV agree exactly.</summary>
    public static class Percentiles
    {
        /// <summary>Nearest-rank percentile of the first <paramref name="count"/> items of an already sorted array.</summary>
        public static double NearestRank(double[] sorted, int count, double p)
        {
            if (count <= 0) return double.NaN;
            int rank = (int)Math.Ceiling(p * count);
            if (rank < 1) rank = 1;
            if (rank > count) rank = count;
            return sorted[rank - 1];
        }

        /// <summary>Sorts <paramref name="samples"/> in place and returns median and p95 of its first <paramref name="count"/> items.</summary>
        public static void MedianAndP95(double[] samples, int count, out double median, out double p95)
        {
            if (count <= 0)
            {
                median = double.NaN;
                p95 = double.NaN;
                return;
            }
            Array.Sort(samples, 0, count);
            median = NearestRank(samples, count, 0.5);
            p95 = NearestRank(samples, count, 0.95);
        }
    }
}
