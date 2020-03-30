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
    using Peak = Tuple<double, double, int?>; // MZ, intensity, charge-if-known.

    class BoxCar_Planner : Planner
    {
        IDictionary<string, string> Parameters = null;

        static double isolation_width = -1;
        int scan_limit_per_cycle = -1;
        static double mz_tolerance = -1; // For time exclusion.

        bool MS1_outstanding = false;

        ConcurrentQueue<ICustomScan> scan_queue;
        IScans ScanManager;

        Stopwatch RunTimer;
        TimeSpan exclusion_time_interval;
        Dictionary<double, TimeSpan> mz_exclusion_intervals;

        TimeSpan MS1_expiration_span;
        Stopwatch MS1_tardiness;

        System.IO.StreamWriter logfile;

        Dictionary<ScanType, List<Peak>> BoxAggregation = new Dictionary<ScanType, List<Peak>>();

        int ScanCount = 2; // 0 and 1 are special values for RunningNumber/ScanNumber.
        Dictionary<long, ScanType> ScanID = new Dictionary<long, ScanType>();
        // Should have that empty itself occaisionally.

        string parameter_file = null;

        public BoxCar_Planner(string parameterfilename, string logfilename)
        {
            parameter_file = parameterfilename;
            Parameters = Parameter_Parser.read_parameters(parameter_file);

            isolation_width = Convert.ToDouble(Parameters["Isolation_Width_Da"]);
            scan_limit_per_cycle = Convert.ToInt32(Parameters["MS2_Per_Cycle"]);
            exclusion_time_interval = TimeSpan.FromSeconds(Convert.ToInt32(Parameters["Exclusion_Time"]));
            mz_tolerance = Convert.ToDouble(Parameters["Exclusion_Width_Da"]);

            logfile = new System.IO.StreamWriter(logfilename);
        }

        public override void Initialize(IScans machine_scan_manager)
        {
            ScanManager = machine_scan_manager;
            

            scan_queue = new ConcurrentQueue<ICustomScan>();

            RunTimer = new Stopwatch();
            RunTimer.Start();
            
            mz_exclusion_intervals = new Dictionary<double, TimeSpan>();

            MS1_tardiness = new Stopwatch();
            MS1_expiration_span = TimeSpan.FromSeconds(3);
        }

        public override void Cleanup()
        {
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
                Console.WriteLine("ERRANT MS1, resubmitting MS1.");
                log_write("MS1 TIMEOUT AT " + RunTimer.Elapsed.ToString());
                MS1_outstanding = false;
            }

            //ICustomScan newscan;
            if (!scan_queue.TryDequeue(out newscan) && MS1_outstanding == false)
            {
                GenerateMS1();
                MS1_tardiness.Restart();
                Debug.Assert(scan_queue.Count == 4);
                Debug.Assert(scan_queue.TryDequeue(out newscan));
                Console.WriteLine("Generated MS1.");
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

        private ICustomScan baseMS1()
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

            return newscan;
        }

        private void GenerateMS1()
        {
            ICustomScan basic_ms1 = baseMS1();
            ScanID.Add(basic_ms1.RunningNumber, ScanType.MS1);

            ICustomScan box_1 = baseMS1();
            box_1.Values["MsxInjectRanges"] = Parameters["Box_1_Ranges"];
            box_1.Values["MsxInjectTarget"] = Parameters["Box_InjectTargets"];
            box_1.Values["MsxInjectMaxITs"] = Parameters["Box_MaxITs"];
            box_1.Values["MsxInjectNCEs"] = Parameters["Box_InjectNCEs"];
            ScanID.Add(box_1.RunningNumber, ScanType.BoxMS1_1);

            ICustomScan box_2 = baseMS1();
            box_2.Values["MsxInjectRanges"] = Parameters["Box_2_Ranges"];
            box_2.Values["MsxInjectTarget"] = Parameters["Box_InjectTargets"];
            box_2.Values["MsxInjectMaxITs"] = Parameters["Box_MaxITs"];
            box_2.Values["MsxInjectNCEs"] = Parameters["Box_InjectNCEs"];
            ScanID.Add(box_2.RunningNumber, ScanType.BoxMS1_2);

            ICustomScan box_3 = baseMS1();
            box_3.Values["MsxInjectRanges"] = Parameters["Box_3_Ranges"];
            box_3.Values["MsxInjectTarget"] = Parameters["Box_InjectTargets"];
            box_3.Values["MsxInjectMaxITs"] = Parameters["Box_MaxITs"];
            box_3.Values["MsxInjectNCEs"] = Parameters["Box_InjectNCEs"];
            ScanID.Add(box_3.RunningNumber, ScanType.BoxMS1_3);

            Console.WriteLine("BOXED." + " " + basic_ms1.RunningNumber.ToString()
                    + " " + box_1.RunningNumber.ToString()
                    + " " + box_2.RunningNumber.ToString()
                    + " " + box_2.RunningNumber.ToString());

            scan_queue.Enqueue(basic_ms1);
            scan_queue.Enqueue(box_1);
            scan_queue.Enqueue(box_2);
            scan_queue.Enqueue(box_3);

            MS1_outstanding = true;

        }

        private void GenerateMS2s(IEnumerable<Peak> mz_sites)
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
            foreach (var precursor in mz_sites)
            {
                //Tuple<double, double> precursor = charge_precursor.Item2;
                //double mz_site = precursor.Item1;  
                //int charge = charge_precursor.Item1;
                double mz_site = precursor.Item1;
                int charge;
                if (precursor.Item3 != null)
                {
                    charge = (int)precursor.Item3;
                } else
                {
                    charge = 2;
                }


                if (assigned_scan_count > scan_limit_per_cycle)
                {
                    break;
                }
                // Setting charge above 5 seems to cause an error; higher-charge
                // peptides can still be found if the charge is set to 5.
                if (mz_site == 0 || charge > 5)
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

                mz_exclusion_intervals[mz_site] = RunTimer.Elapsed;

                ICustomScan newscan = ScanManager.CreateCustomScan();

                

                newscan.SingleProcessingDelay = 10.0D;
                newscan.Values["IsolationRangeLow"] = (mz_site - (isolation_width / 2)).ToString();
                newscan.Values["IsolationRangeHigh"] = (mz_site + (isolation_width / 2)).ToString();
                newscan.Values["FirstMass"] = "100";
                newscan.Values["LastMass"] = "2000";
                newscan.Values["Polarity"] = "0";
                newscan.Values["NCE"] = "27"; // Whether this is set seems to control whether its an MS1 or MS2.
                newscan.Values["NCE_NormCharge"] = charge.ToString();
                newscan.Values["Resolution"] = "15000";
                newscan.Values["AGC_Target"] = "10000";
                newscan.Values["MaxIT"] = "50"; // Is this in ms???????
                newscan.Values["Microscans"] = "1";
                newscan.RunningNumber = ScanCount++;
                ScanID.Add(newscan.RunningNumber, ScanType.MS2);
                Console.WriteLine("Normcharge: " + charge.ToString());
                assigned_scan_count += 1;
                scan_queue.Enqueue(newscan);
            }
            Console.WriteLine("MS2s: Submitted " + assigned_scan_count.ToString() + " out of " + mz_sites.Count() + " " + scan_queue.Count());
        }

        static double OrderPeak(Peak peak)
        {
            if (peak.Item3 == 1) // Discount charge-1 peaks.
            {
                return peak.Item2 / 1000000.0;
            }
            else
            {
                return peak.Item2;
            }
        }

        private List<Peak> BoxesToMS1()
        {
            Debug.Assert(BoxAggregation.Count() == 3);
            Console.WriteLine(BoxAggregation.Count().ToString());
            // Currently sort of crude; just pile all the centroids together.
            // Will cause problems from overlapping regions!

            List<Peak> combined_scan = new List<Peak>();
            foreach (List<Peak> centroids in BoxAggregation.Values)
            {
                combined_scan.AddRange(centroids);
            }

            BoxAggregation.Clear();

            combined_scan.OrderBy(x => x.Item1);

            return combined_scan;
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

            if (scannum > ScanCount)
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
                case ScanType.MS1:
                case ScanType.MS2:
                case ScanType.Auto:
                case ScanType.Unknown:
                    // Only Box-MS1 scans are being used for precursor assignment.
                    // Other scans are merely written to the file automatically.
                    return; 
                case ScanType.BoxMS1_1:
                case ScanType.BoxMS1_2:
                case ScanType.BoxMS1_3:
                    //MS1_outstanding -= 1;
                    Debug.Assert(!BoxAggregation.ContainsKey(scan_id));
                    BoxAggregation.Add(scan_id, Spectral_Processing.ConvertCentroids(scanData.Centroids).ToList());
                    Console.WriteLine("Received MS1 " + MS1_outstanding.ToString() + " " + BoxAggregation.Count().ToString());
                    

                    if (BoxAggregation.Count() >= 3)
                    {
                        log_write("Processing boxes. " + BoxAggregation.Keys.ToString());

                        MS1_outstanding = false;

                        IEnumerable<Peak> scan = BoxesToMS1();

                        List<Peak> molecular_peaks = Spectral_Processing.deisotope_scan(scan);
                        IEnumerable<Peak> valid_precursors = molecular_peaks.Where(x => x.Item1 > 1);

                        IEnumerable<Peak> precursors_by_int = valid_precursors.OrderByDescending(OrderPeak);

                        GenerateMS2s(precursors_by_int);
                    }
                    return;
                default:
                    Debug.Assert(false);
                    return;

            }
        }
    }
}
