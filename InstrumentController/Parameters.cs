using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace InstrumentController
{
    // Will at some point make this optionally convert method file keys to in-code parameter keys,
    // and perhaps handle appropriate types for non-scan parameters.
    class Parameter_Parser
    {
        public static IDictionary<string, string> read_parameters(string parameter_file)
        {
            Dictionary<string, string> pars = new Dictionary<string, string>();

            StreamReader input = File.OpenText(parameter_file);
            string line = null;
            while ((line = input.ReadLine()) != null)
            {
                line = line.Split('#')[0];
                if (line.Count() > 1)
                {
                    string[] words = line.Split(' ');
                    string key = words[0];
                    string value = words[1];
                    pars.Add(key, value);
                }
            }

            return pars;
        }
    }
}
