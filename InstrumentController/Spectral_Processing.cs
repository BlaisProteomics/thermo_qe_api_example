using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Thermo.Interfaces.InstrumentAccess_V1.MsScanContainer;

namespace InstrumentController
{
    using Peak = Tuple<double, double, int?>; // MZ, intensity, charge-if-known.

    class Spectral_Processing
    {
        static double mz_tolerance = 0.01; // Changed from 0.005.
        // Calculated based on extreme peptide compositions at length < ~20 AAs.
        //static Dictionary<int, Tuple<double, double>> isotopic_ratios = new Dictionary<int, Tuple<double, double>>
        //{
        //    {0, new Tuple<double, double>(0.5, 16.2) },
        //    {1, new Tuple<double, double>(1.0, 9.7) },
        //    {2, new Tuple<double, double>(1.4, 20.7) },
        //    {3, new Tuple<double, double>(1.7, 21.2) },
        //    {4, new Tuple<double, double>(2.0, 30.0) },
        //    {5, new Tuple<double, double>(2.4, 25.2) },
        //    {6, new Tuple<double, double>(2.6, 24.0) },
        //    {7, new Tuple<double, double>(2.9, 22.2) },
        //    {8, new Tuple<double, double>(3.0, 20.0) },
        //    {9, new Tuple<double, double>(3.4, 17.0) }
        //};
        static Dictionary<int, Tuple<double, double>> isotopic_ratios = new Dictionary<int, Tuple<double, double>>
        {
            {0, new Tuple<double, double>(0.5, 16.2) },
            {1, new Tuple<double, double>(1.0, 9.7) },
            {2, new Tuple<double, double>(1.0, 20.7) },
            {3, new Tuple<double, double>(1.0, 21.2) },
            {4, new Tuple<double, double>(1.0, 30.0) },
            {5, new Tuple<double, double>(1.5, 25.2) },
            {6, new Tuple<double, double>(1.5, 24.0) },
            {7, new Tuple<double, double>(2.0, 22.2) },
            {8, new Tuple<double, double>(3.0, 20.0) },
            {9, new Tuple<double, double>(3.4, 17.0) }
        };
        static int minimum_required_peaks = 3;
        // Calculated over the mass of a neutron as 1.008664 Da.
        static double[] chargeFractions = new double[] { -1, 1.008664, 0.504332, 0.3362213333333333, 0.252166,
                                                        0.2017328, 0.16811066666666666, 0.14409485714285714, 0.126083 };

        public static List<Peak> deisotope_scan(IEnumerable<Peak> scan)
        {
            List<Envelope> ActiveEnvelopes = new List<Envelope>();
            List<Envelope> InactiveEnvelopes = new List<Envelope>();
            //List<Tuple<int, Tuple<double, double>>> output = new List<Tuple<int, Tuple<double, double>>>();
            List<Peak> output = new List<Peak>();

            foreach (Peak cPt in scan)
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
                    bool recovered_cPt = false;
                    IEnumerable<Envelope> new_inactive = ActiveEnvelopes.Where(x => !x.is_active);
                    ActiveEnvelopes = ActiveEnvelopes.Except(new_inactive).ToList();
                    foreach (Envelope envlp in new_inactive)
                    {
                        // For each envelope that's now past its prime, either add it to the output
                        // list or release its peaks and try to re-assign them.
                        if (envlp.peaks.Count >= minimum_required_peaks)
                        {
                            InactiveEnvelopes.Add(envlp);
                        }
                        else
                        {
                            // Throw out the root peak, though.
                            foreach (Peak peak in envlp.peaks.Skip(1))
                            {
                                Envelope rec_envlp = new Envelope(peak);
                                if (!recovered_cPt)
                                {
                                    recovered_cPt = rec_envlp.try_add_peak(cPt);
                                }
                                ActiveEnvelopes.Add(rec_envlp);

                            }
                        }                       
                    }
                    if (!recovered_cPt)
                    {
                        Envelope new_envlp = new Envelope(cPt);
                        ActiveEnvelopes.Add(new_envlp);
                    }
                }
            }

            InactiveEnvelopes.AddRange(ActiveEnvelopes.Where(e => e.peaks.Count >= minimum_required_peaks));


            foreach (Envelope envlp in InactiveEnvelopes)
            {
                //output.Add(new Tuple<int, Tuple<double, double>>(envlp.charge, envlp.peaks[0]));
                Peak mono_pk = envlp.peaks[0];
                Peak chg_pk = new Peak(mono_pk.Item1, mono_pk.Item2, envlp.charge);
                output.Add(chg_pk);
            }
            //Envelope foo = InactiveEnvelopes.Aggregate((e1, e2) => e1.peakcount > e2.peakcount ? e1 : e2);
            //Debug.Assert(output.Count() == 0);
            return output;
        }

        class Envelope
        {
            public List<Peak> peaks = new List<Peak>();
            public int charge = -1;
            double scope;
            double next_mz;
            public bool is_active = true;

            public Envelope(Peak root)
            {
                peaks.Add(root);
                scope = root.Item1 + chargeFractions[1] + mz_tolerance;
            }

            public bool try_add_peak(Peak newpeak)
            {
                if (newpeak.Item1 > scope)
                {
                    is_active = false;
                    return false;
                }

                if (charge == -1)
                {
                    for (int chg = 1; chg < chargeFractions.Length; chg++)
                    {
                        double chg_next_mz = peaks[0].Item1 + chargeFractions[chg];
                        if (Math.Abs(chg_next_mz - newpeak.Item1) < mz_tolerance)
                        {
                            double ratio = peaks.Last().Item2 / newpeak.Item2;
                            if (ratio > isotopic_ratios[0].Item1 && ratio < isotopic_ratios[0].Item2)
                            {
                                charge = chg;
                                peaks.Add(newpeak);
                                next_mz = newpeak.Item1 + chargeFractions[charge];
                                scope = next_mz + mz_tolerance;
                                return true;
                            }
                        }
                    }
                }
                else
                {
                    if (Math.Abs(next_mz - newpeak.Item1) < mz_tolerance)
                    {
                        double ratio = peaks.Last().Item2 / newpeak.Item2;
                        int peaknum = peaks.Count - 1;
                        if (ratio > isotopic_ratios[peaknum].Item1 && ratio < isotopic_ratios[peaknum].Item2)
                        {
                            peaks.Add(newpeak);
                            next_mz = newpeak.Item1 + chargeFractions[charge];
                            scope = next_mz + mz_tolerance;
                            return true;
                        } 
                    }
                }
                return false;
            }
        }

        //public static IEnumerable<Tuple<double, double>> ConvertCentroids(IEnumerable<ICentroid> centroids)
        //{
        //    return centroids.Select(c => new Tuple<double, double>(c.Mz, c.Intensity));
        //}
        
        public static IEnumerable<Peak> ConvertCentroids(IEnumerable<ICentroid> centroids)
        {
            return centroids.Select(c => new Peak(c.Mz, c.Intensity, c.Charge));
        }
    }
}
