using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Thermo.Interfaces.InstrumentAccess_V1;
using Thermo.Interfaces.InstrumentAccess_V1.Control.Acquisition;
using Thermo.Interfaces.InstrumentAccess_V1.Control.Acquisition.Modes;
using Thermo.Interfaces.ExactiveAccess_V1;
using Thermo.Interfaces.ExactiveAccess_V1.Control;
using Thermo.Interfaces.ExactiveAccess_V1.Control.Acquisition;
using Thermo.Interfaces.ExactiveAccess_V1.Control.Acquisition.Workflow;

using Thermo.Interfaces.InstrumentAccess_V1.MsScanContainer;

using Thermo.Interfaces.InstrumentAccess_V1.Control.Scans;

using IMsScan = Thermo.Interfaces.InstrumentAccess_V1.MsScanContainer.IMsScan;
using ICentroid = Thermo.Interfaces.InstrumentAccess_V1.MsScanContainer.ICentroid;

using System.Diagnostics;


namespace InstrumentController
{
    class DD_Planner_AGC_Threshold : Planner
    {
        IDictionary<string, string> Parameters = null;

        static double isolation_width;
        int scan_limit_per_cycle = 10;
        static double mz_tolerance; // For time exclusion.

        bool MS1_outstanding;

        ConcurrentQueue<ICustomScan> scan_queue;
        IScans ScanManager;

        Stopwatch RunTimer;
        TimeSpan exclusion_time_interval;
        Dictionary<double, TimeSpan> mz_exclusion_intervals;

        TimeSpan MS1_expiration_span;
        Stopwatch MS1_tardiness;

        System.IO.StreamWriter logfile;
        System.IO.StreamWriter scaninfo_file;

        long ScanCount = 10000; // 0 and 1 are special values for RunningNumber/ScanNumber.
        Dictionary<long, ScanType> ScanID = new Dictionary<long, ScanType>();

        string parameter_file = null;

        double PrecursorIntensityThreshold = 0;

        public DD_Planner_AGC_Threshold(string parameterfilename, string logfilename)
        {
            parameter_file = parameterfilename;
            Parameters = Parameter_Parser.read_parameters(parameter_file);

            isolation_width = Convert.ToDouble(Parameters["Isolation_Width_Da"]);
            scan_limit_per_cycle = Convert.ToInt32(Parameters["MS2_Per_Cycle"]);
            exclusion_time_interval = TimeSpan.FromSeconds(Convert.ToInt32(Parameters["Exclusion_Time"]));
            mz_tolerance = Convert.ToDouble(Parameters["Exclusion_Width_Da"]);
            PrecursorIntensityThreshold = Convert.ToDouble(Parameters["Precursor_Threshold"]);

            logfile = new System.IO.StreamWriter(logfilename);
            scaninfo_file = new System.IO.StreamWriter(logfilename + "_SCANINFO.txt");
        }

        public override void Initialize(IScans machine_scan_manager)
        {
            ScanManager = machine_scan_manager;
            scan_queue = new ConcurrentQueue<ICustomScan>();

            MS1_outstanding = false;

            RunTimer = new Stopwatch();
            RunTimer.Start();
            //exclusion_time_interval = TimeSpan.FromSeconds(10);
            mz_exclusion_intervals = new Dictionary<double, TimeSpan>();

            MS1_tardiness = new Stopwatch();
            MS1_expiration_span = TimeSpan.FromSeconds(10);
        }

        public override void Cleanup()
        {
            scaninfo_file.Flush();
            scaninfo_file.Close();
            logfile.Close();
        }

        void log_scan(string label, IEnumerable<ICentroid> scan)
        {
            logfile.WriteLine("SCAN " + label);
            foreach (ICentroid pt in scan)
            {
                logfile.WriteLine(pt.Mz.ToString() + " " + pt.Intensity.ToString() + " " + pt.Charge.ToString());
            }
            logfile.WriteLine("DONE");
            logfile.Flush();
        }

        void log_write(string message)
        {
            logfile.WriteLine(RunTimer.Elapsed.ToString() + ": " + message);
        }

        public override bool assignScan(out ICustomScan newscan)
        {
            newscan = null;
            //Console.WriteLine("Attempting assignment; " + MS1_outstanding.ToString());

            if (scan_queue.IsEmpty && MS1_tardiness.Elapsed > MS1_expiration_span)
            {
                //Debug.Assert(false);
                Console.WriteLine("ERRANT MS1, resubmitting MS1.");
                log_write("MS1 TIMEOUT AT " + RunTimer.Elapsed.ToString());
                MS1_outstanding = false;
            }

            //ICustomScan newscan;
            if (!scan_queue.TryDequeue(out newscan) && !MS1_outstanding)
            {
                GenerateMS1();
                MS1_outstanding = true;
                MS1_tardiness.Restart();
                Debug.Assert(scan_queue.Count == 1);
                Debug.Assert(scan_queue.TryDequeue(out newscan));
                Console.WriteLine("Generated MS1.");
                log_write("Generated MS1.");
            }
            if (newscan == null)
            {
                Console.Write(".");
                //log_write("Waiting for assignment. " + MS1_tardiness.Elapsed.ToString());
                logfile.Flush();
                return false;
            }
            else
            {
                ScanType scantype = ScanType.Unknown;
                ScanID.TryGetValue(newscan.RunningNumber, out scantype);
                Console.WriteLine("Assigning " + scantype.ToString() + " " + newscan.RunningNumber.ToString());
                log_write("Assigning " + scantype.ToString() + " " + newscan.RunningNumber.ToString());
                logfile.Flush();
                return true;
            }
        }

        private void GenerateMS1()
        {
            ICustomScan newscan = ScanManager.CreateCustomScan();
            newscan.SingleProcessingDelay = 10.0D;
            newscan.Values["FirstMass"] = Parameters["MS1_FirstMass"];
            newscan.Values["LastMass"] = Parameters["MS1_LastMass"];
            newscan.Values["Polarity"] = Parameters["MS1_Polarity"];
            newscan.Values["NCE"] = "0"; // Whether this is set seems to control whether its an MS1 or MS2.
            newscan.Values["Resolution"] = Parameters["MS1_Resolution"];
            newscan.Values["AGC_Target"] = Parameters["MS1_AGC_Target"];
            newscan.Values["MaxIT"] = Parameters["MS1_MaxIT"];
            newscan.Values["Microscans"] = Parameters["MS1_Microscans"];
            newscan.RunningNumber = ScanCount++;
            ScanID.Add(newscan.RunningNumber, ScanType.MS1);

            //Console.WriteLine("Submitting |" + scanlabel + "| starting from " + sweep_mz.ToString() + " scan number " + current_scan_number);

            scan_queue.Enqueue(newscan);
            //log_write("Submitting MS1.");
        }

        private void GenerateMS2s(IEnumerable<Tuple<int, Tuple<double, double>>> mz_sites)
        {
            HashSet<double> unexclude_mzs = new HashSet<double>();
            foreach (var mz in mz_exclusion_intervals.Keys)
            {
                if (mz_exclusion_intervals[mz] + exclusion_time_interval < RunTimer.Elapsed)
                {
                    unexclude_mzs.Add(mz);
                }
            }
            Console.WriteLine("Unexcluding " + unexclude_mzs.Count.ToString() + " of " + mz_exclusion_intervals.Count.ToString());
            foreach (var mz in unexclude_mzs)
            {
                mz_exclusion_intervals.Remove(mz);
            }

            int assigned_scan_count = 0;
            int MS1_after_number = scan_limit_per_cycle - 4;
            bool MS1_submitted = false;
            foreach (var precursor in mz_sites)
            {
                double mz_site = precursor.Item2.Item1;
                int charge = precursor.Item1;

                if (assigned_scan_count > scan_limit_per_cycle)
                {
                    break;
                }

                if (mz_site == 0)
                {
                    continue;
                }

                if (precursor.Item2.Item2 < PrecursorIntensityThreshold)
                {
                    continue;
                }

                bool excluded = false;
                foreach (var mz in mz_exclusion_intervals.Keys)
                {
                    if (Math.Abs(mz - mz_site) < mz_tolerance)
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded)
                {
                    continue;
                }

                // Setting charge above 5 seems to cause an error; higher-charge
                // peptides can still be found if the charge is set to 5.
                if (charge > 5)
                {
                    charge = 5;
                }

                mz_exclusion_intervals[mz_site] = RunTimer.Elapsed;



                ICustomScan newscan = ScanManager.CreateCustomScan();
                newscan.SingleProcessingDelay = 10.0D;
                newscan.Values["IsolationRangeLow"] = (mz_site - (isolation_width / 2)).ToString();
                newscan.Values["IsolationRangeHigh"] = (mz_site + (isolation_width / 2)).ToString();
                newscan.Values["FirstMass"] = Parameters["MS2_FirstMass"];
                newscan.Values["LastMass"] = Parameters["MS2_LastMass"];
                newscan.Values["Polarity"] = Parameters["MS2_Polarity"];
                newscan.Values["NCE"] = Parameters["MS2_NCE"]; // Whether this is set seems to control whether its an MS1 or MS2.
                newscan.Values["NCE_NormCharge"] = charge.ToString();
                newscan.Values["Resolution"] = Parameters["MS2_Resolution"];

                newscan.Values["AGC_Target"] = Parameters["MS2_AGC_Target"];
                newscan.Values["MaxIT"] = Parameters["MS2_MaxIT"]; // Is this in ms???????


                newscan.Values["Microscans"] = Parameters["MS2_Microscans"];
                newscan.RunningNumber = ScanCount++;
                ScanID.Add(newscan.RunningNumber, ScanType.MS2);
                //Console.WriteLine("Normcharge: " + charge.ToString());
                assigned_scan_count += 1;
                scan_queue.Enqueue(newscan);
                scaninfo_file.WriteLine(newscan.RunningNumber.ToString() + " " + mz_site.ToString() + " " + charge.ToString() + " " + precursor.Item2.Item2 + "__");

                if ((!MS1_submitted) && assigned_scan_count >= MS1_after_number)
                {
                    GenerateMS1();
                    Console.WriteLine("MS1 submitted midcycle.");
                    log_write("MS1 submitted midcycle.");
                    MS1_submitted = true;
                    MS1_outstanding = true;
                    MS1_tardiness.Restart();
                }
            }
            if (!MS1_submitted)
            {
                GenerateMS1();
                Console.WriteLine("MS1 submitted postcycle.");
                log_write("MS1 submitted postcycle.");
                MS1_submitted = true;
                MS1_outstanding = true;
                MS1_tardiness.Restart();
            }
            Console.WriteLine("MS2s: Submitted " + assigned_scan_count.ToString() + " out of " + mz_sites.Count() + " " + scan_queue.Count());
            log_write("MS2s: Submitted " + assigned_scan_count.ToString() + " out of " + mz_sites.Count() + " " + scan_queue.Count());
        }

        static double OrderPeak(Tuple<int, Tuple<double, double>> peak)
        {
            if (peak.Item1 == 1) // Discount charge-1 peaks.
            {
                return peak.Item2.Item2 / 1000000.0;
            }
            else
            {
                return peak.Item2.Item2;
            }
        }

        public override void receiveScan(IMsScan scanData)
        {
            //IInfoContainer info = scanData.CommonInformation;
            //IInfoContainer more_info = scanData.SpecificInformation;
            //MsScanInformationSource infosource = MsScanInformationSource.Unknown;
            object obj_holder = null;
            ScanType scan_id = ScanType.Unknown;
            int scannum = -1;
            //if (info.TryGetRawValue("Scan", out obj_holder, ref infosource))
            //{
            //    scannum = (int)obj_holder;
            //    if (ScanID.ContainsKey(scannum)) // Auto-scans also have Scan numbers!
            //    {
            //        scan_id = ScanID[scannum];
            //        ScanID.Remove(scannum);
            //    }
            //}
            //object accessid = null;
            if (scanData.SpecificInformation.TryGetRawValue("Access Id:", out obj_holder))
            {
                scannum = (int)obj_holder;
                if (ScanID.ContainsKey(scannum))
                {
                    scan_id = ScanID[scannum];
                    ScanID.Remove(scannum);
                }
            }

            //Debug.Assert(false);
            if (scannum >= ScanCount)
            {
                Console.WriteLine("Adjusting ScanCount: " + scannum.ToString() + " " + ScanCount.ToString());
                log_write("Adjusting ScanCount: " + scannum.ToString() + " " + ScanCount.ToString());
                ScanCount = scannum + 1;
            }
            //else if (scannum + 100 < ScanCount)
            //{
            //    Console.WriteLine("Adjusting ScanCount DOWNWARD: " + scannum.ToString() + " " + ScanCount.ToString());
            //    log_write("Adjusting ScanCount DOWNWARD: " + scannum.ToString() + " " + ScanCount.ToString());
            //    ScanCount = scannum + 1;
            //}

            Console.WriteLine("Received a " + scan_id.ToString() + " " + scannum.ToString());
            log_write("Received a " + scan_id.ToString() + " " + scannum.ToString());

            switch (scan_id)
            {
                case ScanType.MS2:
                case ScanType.Auto:
                case ScanType.Unknown:
                    return;
                case ScanType.MS1:
                    MS1_outstanding = false;

                    IEnumerable<Tuple<double, double>> scan = Spectral_Processing.ConvertCentroids(scanData.Centroids);
                    List<Tuple<int, Tuple<double, double>>> molecular_peaks = Spectral_Processing.deisotope_scan(scan);
                    IEnumerable<Tuple<int, Tuple<double, double>>> valid_precursors = molecular_peaks.Where(x => x.Item1 > 1);

                    IEnumerable<Tuple<int, Tuple<double, double>>> precursors_by_int = valid_precursors.OrderByDescending(OrderPeak);

                    GenerateMS2s(precursors_by_int);
                    Console.WriteLine("MS1 stats: " + scan.Count() + " " + valid_precursors.Count());
                    log_write("MS1 stats: " + scan.Count() + " " + valid_precursors.Count());
                    return;
                default:
                    Debug.Assert(false);
                    return;
            }
        }
    }
}
