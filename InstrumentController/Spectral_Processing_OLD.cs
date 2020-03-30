using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;

using Thermo.Interfaces.InstrumentAccess_V1;
using Thermo.Interfaces.InstrumentAccess_V1.Control.Acquisition;
using Thermo.Interfaces.InstrumentAccess_V1.Control.Acquisition.Modes;
using Thermo.Interfaces.ExactiveAccess_V1;
using Thermo.Interfaces.ExactiveAccess_V1.Control;
using Thermo.Interfaces.ExactiveAccess_V1.Control.Acquisition;
using Thermo.Interfaces.ExactiveAccess_V1.Control.Acquisition.Workflow;

using Thermo.Interfaces.InstrumentAccess_V1.MsScanContainer;

using Thermo.Interfaces.InstrumentAccess_V1.Control.Scans;

namespace InstrumentController
{
    class Spectral_Processing_OLD
    {
        static double mz_tolerance = 0.005;
        // Calculated based on extreme peptide compositions at length < ~20 AAs.
        static Dictionary<int, Tuple<double, double>> isotopic_ratios = new Dictionary<int, Tuple<double, double>>
        {
            {0, new Tuple<double, double>(0.5, 16.2) },
            {1, new Tuple<double, double>(1.0, 9.7) },
            {2, new Tuple<double, double>(1.4, 20.7) },
            {3, new Tuple<double, double>(1.7, 21.2) },
            {4, new Tuple<double, double>(2.0, 30.0) },
            {5, new Tuple<double, double>(2.4, 25.2) },
            {6, new Tuple<double, double>(2.6, 24.0) },
            {7, new Tuple<double, double>(2.9, 22.2) },
            {8, new Tuple<double, double>(3.0, 20.0) },
            {9, new Tuple<double, double>(3.4, 17.0) }
        };
        static int minimum_required_peaks = 2;
        // Calculated over the mass of a neutron as 1.008664 Da.
        static double[] chargeFractions = new double[] { -1, 1.008664, 0.504332, 0.3362213333333333, 0.252166,
                                                        0.2017328, 0.16811066666666666, 0.14409485714285714, 0.126083 };

        public static List<Tuple<int, Tuple<double, double>>> deisotope_scan(IEnumerable<Tuple<double, double>> scan)
        {
            List<Envelope> ActiveEnvelopes = new List<Envelope>();
            List<Envelope> InactiveEnvelopes = new List<Envelope>();
            List<Tuple<int, Tuple<double, double>>> output = new List<Tuple<int, Tuple<double, double>>>();

            foreach (Tuple<double, double> cPt in scan)
            {
                bool assigned = false;
                foreach (Envelope envlp in ActiveEnvelopes)
                {
                    if (!envlp.is_active)
                    {
                        continue;
                    }
                    if (envlp.try_add_peak(cPt))
                    {
                        assigned = true;
                        break;
                    }
                }

                if (!assigned)
                {
                    IEnumerable<Envelope> new_inactive = ActiveEnvelopes.Where(x => !x.is_active);
                    if (new_inactive.Any())
                    {
                        InactiveEnvelopes.AddRange(new_inactive);
                        ActiveEnvelopes = ActiveEnvelopes.Except(new_inactive).ToList();
                    }

                    Envelope new_envlp = new Envelope(cPt);
                    ActiveEnvelopes.Add(new_envlp);
                }
            }

            InactiveEnvelopes.AddRange(ActiveEnvelopes);


            foreach (Envelope envlp in InactiveEnvelopes)
            {
                // Choose the charge that got the most peaks; if that number meets the required number of
                // peaks, add it to the output list.
                int charge = envlp.peaks.Keys.Aggregate((c1, c2) => envlp.peaks[c1].Count() > envlp.peaks[c2].Count() ? c1 : c2);
                //int charge = envlp.peaks.Keys.Max(c => envlp.peaks[c].Count());
                if (envlp.peaks[charge].Count() > minimum_required_peaks)
                {
                    output.Add(new Tuple<int, Tuple<double, double>>(charge, envlp.peaks[charge][0]));
                }
                //IEnumerable<int> c_by_mag = envlp.peaks.Keys.OrderByDescending(c => envlp.peaks[c].Select(p => p.Item2).Sum());

                //// Allows envelopes that share peaks!
                //foreach (int charge in envlp.peaks.Keys)
                //{
                //    if (envlp.peaks[charge].Count() >= minimum_required_peaks)
                //    {
                        
                //    }
                //}
            }
            //Envelope foo = InactiveEnvelopes.Aggregate((e1, e2) => e1.peakcount > e2.peakcount ? e1 : e2);
            //Debug.Assert(output.Count() == 0);
            return output;
        }


        class Envelope
        {
            public Dictionary<int, List<Tuple<double, double>>> peaks = new Dictionary<int, List<Tuple<double, double>>>();
            double[] next_mz = new double[chargeFractions.Length];
            public bool is_active = true;
            public int peakcount = 0;

            public Envelope(Tuple<double, double> root)
            {
                for (int i = 1; i < chargeFractions.Length; i++)
                {
                    peaks[i] = new List<Tuple<double, double>>();
                    peaks[i].Add(root);
                    next_mz[i] = root.Item1 + chargeFractions[i];
                }
            }

            public bool try_add_peak(Tuple<double, double> peak)
            {
                bool added_peak = false;
                is_active = false;
                for (int chg = 1; chg < next_mz.Length; chg++)
                {
                    if (Math.Abs(peak.Item1 - next_mz[chg]) < mz_tolerance)
                    {
                        double ratio = peaks[chg].Last().Item2 / peak.Item2;
                        int peaknum = peaks[chg].Count - 1;
                        if (ratio > isotopic_ratios[peaknum].Item1 && ratio < isotopic_ratios[peaknum].Item2)
                        {
                            peaks[chg].Add(peak);
                            //Debug.Assert((peaks[chg].Count() < 3) || (peaks[chg].First().Item2 > peak.Item2));
                            //Debug.Assert(peaks[chg].Count() < 3 || ratio > 1);
                            next_mz[chg] = peak.Item1 + chargeFractions[chg];
                            added_peak = true;
                            
                        }
                    }
                    else if (next_mz[chg] + mz_tolerance > peak.Item1)
                    {
                        is_active = true;
                    }
                    
                }
                if (added_peak) { peakcount += 1; }
                return added_peak;
            }
        }

        public static IEnumerable<Tuple<double, double>> ConvertCentroids(IEnumerable<ICentroid> centroids)
        {
            return centroids.Select(c => new Tuple<double, double>(c.Mz, c.Intensity));
        }

        static string spectrum_log = "C:/Xcalibur/data/spectrum_log.txt";
        static void LogSpectrum(IEnumerable<ICentroid> spectrum, string idnum)
        {
            spectrum.OrderBy(x => x.Mz);
            string[] peaklines = new string[spectrum.Count()];
            int i = 0;
            foreach (ICentroid pt in spectrum)
            {
                peaklines[i] = pt.Mz.ToString() + " " + pt.Intensity.ToString();
            }
            File.AppendAllText(spectrum_log, "#" + idnum + "\n" + String.Join("\n", peaklines) + "\n");
        }
    }
}
