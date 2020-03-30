using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO;
using System.Globalization;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Win32;

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
using InstrumentController;

using Thermo.Interfaces.InstrumentAccess_V1.Control.InstrumentValues;




namespace InstrumentController
{
    using Scantuple = Tuple<double, double, double, double, double>;
    using Peak = Tuple<double, double, int?>; // MZ, intensity, charge-if-known.


    class MainControl
    {
        static void Main_Peakpick_Test(string[] args)
        {
            Console.WriteLine("Peakpicking test!");

            string spectrumfile = "//rc-data1/blaise/ms_data_share/Max/QE_API/peakpick_testing/segment.txt";
            string outputfile = "//rc-data1/blaise/ms_data_share/Max/QE_API/peakpick_testing/seg_output.txt";
            List<Peak> spectrum_agg = new List<Peak>();
            int scan_number = -1;
            StreamReader spectrum_data = File.OpenText(spectrumfile);
            StreamWriter peak_output = new StreamWriter(outputfile);
            string line = null;
            while ((line = spectrum_data.ReadLine()) != null)
            {
                if (line.Contains('#'))
                {
                    List<Peak> pickedpeaks = Spectral_Processing.deisotope_scan(spectrum_agg);
                    peak_output.WriteLine("SCAN {0}", scan_number.ToString());
                    foreach (Peak charge_peak in pickedpeaks)
                    {
                        peak_output.WriteLine("{0} {1} {2}",
                            charge_peak.Item1,
                            charge_peak.Item2,
                            charge_peak.Item3);
                    }
                    peak_output.WriteLine("#");
                    Console.WriteLine(scan_number.ToString() + " " + pickedpeaks.Count().ToString());
                }
                else if (line.Contains("SCAN"))
                {
                    string scannumstr = line.Split(' ')[1];
                    Debug.Assert(Int32.TryParse(scannumstr, out scan_number));
                    spectrum_agg.Clear();
                }
                else
                {
                    string[] words = line.Split(' ');
                    Debug.Assert(words.Length == 2);
                    double mz = -1;
                    double intensity = -1;
                    Debug.Assert(Double.TryParse(words[0], out mz));
                    Debug.Assert(Double.TryParse(words[1], out intensity));
                    spectrum_agg.Add(new Peak(mz, intensity, null));
                }
            }
            peak_output.Close();
        }

        static void Main(string[] args)
        {
            Console.WriteLine("QE SuperMethodApp Version: April 5th.");

            string filename;
            string parameterfile;
            bool automatic_mode;
            if (args.Length > 0)
            {
                filename = args[1];
                parameterfile = args[0];
                automatic_mode = true;
            } else
            {
                filename = "C:/Xcalibur/data/test_raw_file.raw";
                parameterfile = "//rc-data1.dfci.harvard.edu/blaise/ms_data_share/Max/QE_API/ParameterFiles/QE_DD.txt";
                automatic_mode = false;
            }

            Debug.Assert(File.Exists(parameterfile));
            IDictionary<string, string> parameters = Parameter_Parser.read_parameters(parameterfile);

            string logfilename = String.Concat(filename, "_log.txt");

            if (!parameters.ContainsKey("MethodType"))
            {
                throw new System.ArgumentException("Parameter file must contain a 'MethodType' line.");
            }
            Planner run_planner = null;
            switch (parameters["MethodType"])
            {
                case "DataDependent":
                    run_planner = new DD_Planner(parameterfile, logfilename);
                    Console.WriteLine("Data-Dependent Mode.");
                    break;
                //case "DataDependent_AGC":
                //    run_planner = new DD_Planner_AGC(parameterfile, logfilename);
                //    Console.WriteLine("Data-Dependent AGC-Enabled Mode.");
                //    break;
                //case "DataDependent_AGC_Threshold":
                //    run_planner = new DD_Planner_AGC_Threshold(parameterfile, logfilename);
                //    Console.WriteLine("Data-Dependent AGC-Threshold-Enabled Mode.");
                //    break;
                case "BoxCar":
                    run_planner = new BoxCar_Planner(parameterfile, logfilename);
                    Console.WriteLine("BoxCar Mode.");
                    break;
                default:
                    throw new System.ArgumentException("Invalid MethodType in parameter file: {0}",
                                                        parameters["MethodType"]);
            }
            Debug.Assert(run_planner != null);

            Machinist machine_obj = new Machinist();
            machine_obj.RunMachine(run_planner, filename, automatic_mode);
        }
    }


    enum ScanType { Unknown, MS1, MS2, MS3, BoxMS1_1, BoxMS1_2, BoxMS1_3,
                    Inhib_MS1, Hierarchy_MS1, Auto};
    
    abstract class Planner // Doing it proper.
    {
        public abstract void Initialize(IScans machine_scan_manager);
        public abstract bool assignScan(out ICustomScan newscan);
        public abstract void receiveScan(IMsScan scanData);
        public abstract void Cleanup();
    }

    class Machinist
    {
        int intended_run_seconds = 115 * 60;
        int column_wait_time_seconds = 10 * 60;
        bool USE_CONTACT_CLOSURE = true;
        // Eventually, put these in parameter files!

        int working_voltage = 2500;

        

        Planner runProgram;

        IInstrumentAccessContainer container;
        IInstrumentAccess instrument;
        IExactiveInstrumentAccess iexactive;
        IExactiveControl control;
        IExactiveAcquisition acquisition;

        IScans scanner;

        IValue machine_voltage;
        double current_voltage;

        Stopwatch runTimeKeeper;
        TimeSpan intendedRunTime;
        int scan_count = 0;

        bool authorized_for_run = false;
        bool accepting_scans = false;
        bool run_is_active = false;

        //private static Mutex scan_submit_MUT = new Mutex();
        //private static Mutex scan_receive_MUT = new Mutex();
        private static Mutex planner_MUT = new Mutex();

        private static Semaphore can_submit_scan = new Semaphore(1, 1);

        //bool can_submit_scan = false;

        public void writeScanInfo(IInfoContainer junk)
        {
            object oo = null;
            string ss = null;
            MsScanInformationSource ii = MsScanInformationSource.Unknown;
            foreach (string name in junk.Names)
            {
                if (junk.TryGetValue(name, out ss, ref ii))
                {
                    if (junk.TryGetRawValue(name, out oo, ref ii))
                    {
                        if (oo.GetType() == typeof(string))
                        {
                            Console.WriteLine("\t{0}: type={1}, text='{2}', raw='{3}",
                                              name, ii, ss, oo);
                        }
                        else if (oo is System.Double[])
                        {
                            Console.WriteLine("\t{0}:", name);
                            foreach (var item in (oo as System.Double[]))
                            {
                                Console.WriteLine("\t\t{0}", item.ToString());
                            }
                            Console.WriteLine("\t{0} Done.", name);
                        }
                        else
                        {
                            Console.WriteLine("\t{0}: Not rendering {1} {2}.", name, oo.GetType().ToString(), oo.ToString());
                        }

                    }
                }
            }
        }

        void voltageChangeResponse(object sender, Thermo.Interfaces.InstrumentAccess_V1.Control.ContentChangedEventArgs args)
        {
            if (machine_voltage.Content.Content == null)
            {
                Console.WriteLine("Null machine voltage callback!");
                Thread.Sleep(30);
            }
            current_voltage = Double.Parse(machine_voltage.Content.Content);
            //current_voltage = Int32.Parse(machine_voltage.Content.Content);
        }

        void change_voltage(double volts)
        {
            if (volts == current_voltage)
            {
                Console.WriteLine("Voltage is {0}", current_voltage);
                return;
            }
            Console.WriteLine("Changing voltage from {0} to {1}...", current_voltage.ToString(), volts.ToString());

            double prev_volts = current_voltage;

            int i = 0;
            while (Math.Abs(current_voltage - volts) > 1)
            {
                while (!machine_voltage.Set(volts.ToString()))
                {
                    Console.WriteLine("Set volts to " + volts.ToString() + " returned false.");
                    Thread.Sleep(100);
                }
                Thread.Sleep(100);
                Console.WriteLine("Waiting for voltage change... ({0} -> {1})", prev_volts.ToString(), volts.ToString());
                if (i >= 5)
                {
                    current_voltage = Double.Parse(machine_voltage.Content.Content);
                    Console.WriteLine("Reread voltage. " + current_voltage.ToString());
                    i = 0;
                }
                
            }
            Console.WriteLine("Confirmed switch from " + prev_volts.ToString() + " to " + current_voltage.ToString() + "(" + volts.ToString() + ").");
        }

        System.IO.StreamWriter logfile = new System.IO.StreamWriter("C:/Xcalibur/data/machinist_test_log2.txt");

        void goahead_Response(object sender, MsAcquisitionOpeningEventArgs e)
        {
            Console.WriteLine("Received opening event.");
            authorized_for_run = true;
        }
        void stop_Response(object sender, object e)
        {
            Console.WriteLine("Received closing event.");
            if (run_is_active)
            {
                Console.WriteLine("Run stopped.");
            }
            run_is_active = false;
            authorized_for_run = false;
        }

        void scanArrived_Response(object sender, MsScanEventArgs e)
        {
            try
            {
                Console.WriteLine("Scan arrived.");
                logfile.WriteLine("Scan arrived.");
                if (!authorized_for_run)
                {
                    Console.WriteLine("Ignoring pre-emptive scan.");
                    logfile.WriteLine("Ignoring pre-emptive scan.");
                    return;
                }
                else if (!accepting_scans)
                {
                    Console.WriteLine("Ignoring wait-time scan.");
                    logfile.WriteLine("Ignoring wait-time scan.");
                    return;
                }
                IMsScan scandata = e.GetScan();

                if (runTimeKeeper.Elapsed > intendedRunTime)
                {
                    Console.WriteLine("Ignoring extraneous scan.");
                    logfile.WriteLine("Ignoring extraneous scan.");
                }
                else
                {
                    Console.Write('<');
                    planner_MUT.WaitOne();
                    Console.Write('_');
                    runProgram.receiveScan(scandata);
                    scandata.Dispose(); 
                    planner_MUT.ReleaseMutex();
                    Console.Write('>');
                    logfile.Flush();
                }
            }
            catch (Exception err)
            {
                Console.WriteLine("Caught error in scanArrived_response!  Cancelling voltage.");
                change_voltage(0);
                Console.WriteLine(err.ToString());
                throw;
            }

        }

        void readyForScan_Response(object sender, EventArgs e)
        {
            Console.WriteLine("Got ready event.");
            logfile.WriteLine("Got ready event.");
            try
            {
                can_submit_scan.Release();
            } catch (SemaphoreFullException err)
            {
                Console.WriteLine("Redundant ready event.");
                logfile.WriteLine("Redundant ready event.");
            }
            
        }

        // Run in non-main thread, so that main thread can still receive events.
        void scan_assignment_handler()
        {
            submitScan();
            while (accepting_scans)
            {
                //Console.WriteLine("Submitting.");
                logfile.WriteLine("Submitting.");
                submitScan();
            }
        }

        void submitScan() // Separate from readyForScan_Response so that it can be called directly to begin custom scans.
        {
            try
            {
                acquisition.WaitFor(TimeSpan.FromSeconds(0.5), SystemMode.On, SystemMode.DirectControl);
                ICustomScan newscan = null;
                while (accepting_scans && newscan == null)
                {
                    //Console.WriteLine("Entering mutex2.");
                    planner_MUT.WaitOne();
                    //Console.WriteLine("Got mutex2.");
                    runProgram.assignScan(out newscan);
                    //Console.WriteLine("Leaving mutex2.");
                    planner_MUT.ReleaseMutex();
                    //Console.WriteLine("Out of mutex2.");
                    Thread.Sleep(5);
                }
                //bool result = scanner.SetCustomScan(newscan);
                //Console.WriteLine("Waiting to submit.");
                logfile.WriteLine("Waiting to submit.");
                can_submit_scan.WaitOne(); // WaitOne also grabs a semaphore-unit, apparently.
                                           //Console.WriteLine("Initiating submission.");
                logfile.WriteLine("Initiating submission.");
                while (accepting_scans && !scanner.SetCustomScan(newscan))
                {
                    Thread.Sleep(5);
                }
                //Console.WriteLine("Submitted.");
                logfile.WriteLine("Submitted.");
            }
            catch (Exception err)
            {
                Console.WriteLine("Caught exception in submitScan!  Cancelling voltage.");
                change_voltage(0);
                Console.WriteLine(err.ToString());
                throw;
            }

        }


        void send_inaugural_scan()
        {
            ICustomScan scan = scanner.CreateCustomScan();
            scan.SingleProcessingDelay = 5.0D;
            scan.Values["FirstMass"] = "345";
            scan.Values["LastMass"] = "789";
            scan.Values["Polarity"] = "0";
            scan.Values["NCE"] = "0"; // Whether this is set seems to control whether its an MS1 or MS2.
            scan.Values["Resolution"] = "15000";
            scan.Values["AGC_Target"] = "10000";
            scan.Values["MaxIT"] = "10";
            scan.Values["Microscans"] = "1";

            can_submit_scan.WaitOne();
            while (!scanner.SetCustomScan(scan))
            {
                Thread.Sleep(5);
            }
            Console.WriteLine("Submitted starting scan.");
            

        }


        void note_state_change(object sender, EventArgs e)
        {
            Console.WriteLine("State change event: " + acquisition.State.SystemState);
        }


  

        public void RunMachine(Planner runManager, string filename, bool auto)
        {
            runProgram = runManager;

            // Instrument access setup stuff.
            if (!auto)
            {
                Console.WriteLine("Ready. (Press any key.)");
                Console.ReadKey();
            }


            string device_registration = ((IntPtr.Size > 4) ? @"SOFTWARE\Wow6432Node\Finnigan\Xcalibur\Devices\" : @"SOFTWARE\Finnigan\Xcalibur\Devices\") + "Thermo Exactive";
            string asmName = "None";
            string typeName = "None";

            RegistryKey key = Registry.LocalMachine.OpenSubKey(device_registration);
            Debug.Assert(key != null);
            asmName = (string)key.GetValue("ApiFileName_Clr2_32_V1", null);
            typeName = (string)key.GetValue("ApiClassName_Clr2_32_V1", null);

            Console.WriteLine("ASM: " + asmName + "\nType: " + typeName);

            Directory.SetCurrentDirectory(Path.GetDirectoryName(asmName));
            Assembly asm = Assembly.LoadFrom(asmName);
            object api_obj = asm.CreateInstance(typeName);

            container = api_obj as IInstrumentAccessContainer;
            Debug.Assert(container != null);

            instrument = container.Get(1);
            Debug.Assert(instrument != null);

            iexactive = instrument as IExactiveInstrumentAccess;
            Debug.Assert(iexactive != null);

            control = iexactive.Control;
            acquisition = control.Acquisition as IExactiveAcquisition;
            scanner = control.GetScans(false);

            runProgram.Initialize(scanner);

            // Attaching a simple function to on-new-scan event; equivalent of wx.Bind.
            IMsScanContainer scancontainer = iexactive.GetMsScanContainer(0);
            scancontainer.AcquisitionStreamOpening += new EventHandler<MsAcquisitionOpeningEventArgs>(goahead_Response);
            scancontainer.AcquisitionStreamClosing += new EventHandler(stop_Response);
            scancontainer.MsScanArrived += new EventHandler<MsScanEventArgs>(scanArrived_Response);
            scanner.CanAcceptNextCustomScan += new EventHandler(readyForScan_Response);

            acquisition.StateChanged += new EventHandler<StateChangedEventArgs>(note_state_change);

            machine_voltage = control.InstrumentValues.Get("SourceSprayVoltage");
            machine_voltage.ContentChanged += new EventHandler<Thermo.Interfaces.InstrumentAccess_V1.Control.ContentChangedEventArgs>(voltageChangeResponse);
            Thread.Sleep(100); // Gives machine_voltage a chance to get its act together.
            current_voltage = Double.Parse(machine_voltage.Content.Content);
            change_voltage(0);

            // Submitting method and running.
            Console.WriteLine("Starting State=" + acquisition.State.SystemState);
            // Attempts to control machine state; should be "On" after this code block.
            ChangeResult set_to_standby_result = acquisition.SetMode(acquisition.CreateForcedStandbyMode());
            acquisition.WaitFor(TimeSpan.FromSeconds(3), SystemMode.Standby);
            ChangeResult set_to_on_result = acquisition.SetMode(acquisition.CreateOnMode());
            acquisition.WaitFor(TimeSpan.FromSeconds(3), SystemMode.On);

            authorized_for_run = false;
            accepting_scans = false;

            IAcquisitionWorkflow methodWorkflow = null;
            methodWorkflow = acquisition.CreatePermanentAcquisition();
            methodWorkflow.RawFileName = filename; // Numbers are appended to file name on overwrite.
            if (USE_CONTACT_CLOSURE)
            {
                ITrigger ccTrigger = acquisition.CreateTrigger("WaitForContactClosure");
                methodWorkflow.Trigger = ccTrigger;
            } else
            {
                authorized_for_run = true;
                Console.WriteLine("NON-CONTACT CLOSURE START.");
            }

            
            ChangeResult start_acq_result = acquisition.StartAcquisition(methodWorkflow);
            //methodWorkflow.SingleProcessingDelay = 600.0D; // Doesn't work!
            run_is_active = true;

            intendedRunTime = TimeSpan.FromSeconds(intended_run_seconds);
            runTimeKeeper = new Stopwatch();
            

            Console.WriteLine("Waiting for goahead...");
            while (!authorized_for_run)
            {
                Thread.Sleep(100);
            }
            Console.WriteLine("Got goahead.");
            Thread.Sleep(column_wait_time_seconds * 1000);
            Console.WriteLine("Column wait over, setting charge up.");
            change_voltage(working_voltage);

            bool got_to_workable_state = acquisition.WaitFor(TimeSpan.FromSeconds(5), SystemMode.On, SystemMode.DirectControl);
            if (!got_to_workable_state)
            {
                Console.WriteLine("Invalid state " + acquisition.State.SystemMode + " before scan submission.  Done.");
                if (!auto)
                {
                    Console.ReadKey();
                }
                Environment.Exit(0);
            }

            Console.WriteLine("Starting.");

            runTimeKeeper.Start(); // This had been before contact closure confirmation!

            accepting_scans = true;
            Thread scan_handler = new Thread(scan_assignment_handler);
            scan_handler.Start();


            //Debug.Assert(!acquisition.WaitFor(intendedRunTime, SystemMode.Standby)); // Wait while things run; state shouldn't change.
            // COULD PROBABLY do something with AcquisitionStreamClosing instead.
            Console.WriteLine("In run loop.");
            while (runTimeKeeper.Elapsed < intendedRunTime)
            {
                Thread.Sleep(100);
            }
            Console.WriteLine("Closing up.");
            authorized_for_run = false;
            accepting_scans = false;
            

 
            //run_is_active = false;
            //scan_handler.Abort();
            scan_handler.Join();
            Console.WriteLine("Joined.");

            change_voltage(0);

            ChangeResult cancel_result = acquisition.CancelAcquisition();
            Console.WriteLine("Cancellation result: " + cancel_result.ToString());

            Console.WriteLine("Setting mode to standby.");
            ChangeResult setmode2_result = acquisition.SetMode(acquisition.CreateForcedStandbyMode());
            Console.WriteLine("Set mode result: " + setmode2_result.ToString());

            //if (run_is_active)
            //{
            //    Console.WriteLine("Acquisition closed immediately/already.");
            //} else
            //{
            //    Console.WriteLine("Waiting for acquisition close event.");
            //    Stopwatch CloseTimer = new Stopwatch();
            //    CloseTimer.Start();
            //    while (run_is_active)
            //    {
            //        Thread.Sleep(100);
            //    }
            //    Console.WriteLine("Close event received after " + CloseTimer.Elapsed.ToString() + " seconds.");

            //}
            runManager.Cleanup();

            Console.WriteLine("Safety wait.");
            Thread.Sleep(15 * 1000); // Should match SingleProcessingDelay used by Planner.

            Console.WriteLine("Done.");
            if (!auto)
            {
                Console.ReadKey();
            }

            Environment.Exit(0);
        }
    }


 



 
}
