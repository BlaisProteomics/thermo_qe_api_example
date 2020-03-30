using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Not yet!  Exclusion lists are still handled as dicts in the planner objects.

namespace InstrumentController
{
    using Peak = Tuple<double, double, int?>; // MZ, intensity, charge-if-known.

    abstract class ExclusionList
    {
        public abstract void Unexclude(TimeSpan currentTime);
        public abstract void Exclude(TimeSpan currentTime, Peak precursor);
        public abstract bool Is_Excluded(TimeSpan currentTime, Peak precursor);
    }

    class Basic_ExclusionList : ExclusionList
    {
        double mz_tolerance;
        TimeSpan exclusion_interval;
        IDictionary<double, TimeSpan> mz_exclusion_intervals;

        public Basic_ExclusionList(TimeSpan exclusion_time_interval, double mz_tolerance_set)
        {
            mz_tolerance = mz_tolerance_set;
            exclusion_interval = exclusion_time_interval;
            mz_exclusion_intervals = new Dictionary<double, TimeSpan>();
        }

        public override void Exclude(TimeSpan currentTime, Peak precursor)
        {
            double mz = precursor.Item1;
            mz_exclusion_intervals[precursor.Item1] = currentTime + exclusion_interval;
        }

        public override bool Is_Excluded(TimeSpan currentTime, Peak precursor)
        {
            double mz_site = precursor.Item1;
            foreach (var mz in mz_exclusion_intervals.Keys)
            {
                if (Math.Abs(mz - mz_site) < mz_tolerance)
                {
                    return true;
                }
            }
            return false;
        }

        public override void Unexclude(TimeSpan currentTime)
        {
            HashSet<double> unexclude_mzs = new HashSet<double>();
            foreach (var mz in mz_exclusion_intervals.Keys)
            {
                if (mz_exclusion_intervals[mz] <= currentTime)
                {
                    unexclude_mzs.Add(mz);
                }
            }
            Console.WriteLine("Unexcluding " + unexclude_mzs.Count.ToString() + 
                " of " + mz_exclusion_intervals.Count.ToString());
            foreach (var mz in unexclude_mzs)
            {
                mz_exclusion_intervals.Remove(mz);
            }
        }
    }

    class Charge_ExclusionList : ExclusionList
    {
        double mz_tolerance;
        TimeSpan exclusion_interval;
        IDictionary<Peak, TimeSpan> excluded_peaks;

        public Charge_ExclusionList(TimeSpan exclusion_time_interval, double mz_tol)
        {
            mz_tolerance = mz_tol;
            exclusion_interval = exclusion_time_interval;
            excluded_peaks = new Dictionary<Peak, TimeSpan>();
        }

        public override void Exclude(TimeSpan current_time, Peak precursor)
        {
            excluded_peaks[precursor] = current_time + exclusion_interval;
        }

        public override bool Is_Excluded(TimeSpan current_time, Peak precursor)
        {
            foreach (var prev_prec in excluded_peaks.Keys)
            {
                if (Math.Abs(prev_prec.Item1 - precursor.Item1) < mz_tolerance
                    &&
                    (prev_prec.Item3 == null || prev_prec.Item3 == precursor.Item3)) // Charge.
                {
                    return true;
                }
            }
            return false;
        }

        public override void Unexclude(TimeSpan current_time)
        {
            HashSet<Peak> unexclude_mzs = new HashSet<Peak>();
            foreach (var prec in excluded_peaks.Keys)
            {
                if (excluded_peaks[prec] <= current_time)
                {
                    unexclude_mzs.Add(prec);
                }
            }
            Console.WriteLine("Unexcluding " + unexclude_mzs.Count.ToString() +
                " of " + excluded_peaks.Count.ToString());
            foreach (var prec in unexclude_mzs)
            {
                excluded_peaks.Remove(prec);
            }
        }
    }

    class Intensity_ExclusionList : ExclusionList
    {
        double mz_tolerance;
        TimeSpan exclusion_interval;
        IDictionary<Peak, TimeSpan> mz_exclusion_intervals;
        double multiple_limit;

        public Intensity_ExclusionList(TimeSpan exclusion_time_interval,
            double mz_tolerance_set, double multiple)
        {
            mz_tolerance = mz_tolerance_set;
            exclusion_interval = exclusion_time_interval;
            mz_exclusion_intervals = new Dictionary<Peak, TimeSpan>();
            multiple_limit = multiple;
        }

        public override void Exclude(TimeSpan currentTime, Peak precursor)
        {
            mz_exclusion_intervals[precursor] = currentTime + exclusion_interval;
        }

        public override bool Is_Excluded(TimeSpan currentTime, Peak precursor)
        {
            double mz_site = precursor.Item1;
            double overpower_int = precursor.Item2 / multiple_limit;
            foreach (var peak in mz_exclusion_intervals.Keys)
            {
                double mz = peak.Item1;
                double intensity = peak.Item2;
                if (Math.Abs(mz - mz_site) < mz_tolerance
                    && overpower_int < intensity)
                {
                    return true;
                }
            }
            return false;
        }

        public override void Unexclude(TimeSpan currentTime)
        {
            HashSet<Peak> unexclude_mzs = new HashSet<Peak>();
            foreach (var peak in mz_exclusion_intervals.Keys)
            {
                if (mz_exclusion_intervals[peak] <= currentTime)
                {
                    unexclude_mzs.Add(peak);
                }
            }
            Console.WriteLine("Unexcluding " + unexclude_mzs.Count.ToString() +
                " of " + mz_exclusion_intervals.Count.ToString());
            foreach (var peak in unexclude_mzs)
            {
                mz_exclusion_intervals.Remove(peak);
            }
        }
    }
}
