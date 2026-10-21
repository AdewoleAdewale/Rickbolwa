using System;
using System.Collections.Generic;
using System.Text;

namespace Rickbolwa.Services
{
    /// <summary>
    /// Minimal Code 128 encoder (Set C for even-length digit strings, otherwise Set B).
    /// Returns a list of modules: true = bar, false = space. No third-party packages needed.
    /// </summary>
    public static class Code128
    {
        private static readonly string[] Patterns =
        {
            "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213",
            "221312","231212","112232","122132","122231","113222","123122","123221","223211","221132",
            "221231","213212","223112","312131","311222","321122","321221","312212","322112","322211",
            "212123","212321","232121","111323","131123","131321","112313","132113","132311","211313",
            "231113","231311","112133","112331","132131","113123","113321","133121","313121","211331",
            "231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
            "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214",
            "112412","122114","122411","142112","142211","241211","221114","413111","241112","134111",
            "111242","121142","121241","114212","124112","124211","411212","421112","421211","212141",
            "214121","412121","111143","111341","131141","114113","114311","411113","411311","113141",
            "114131","311141","411131","211412","211214","211232","2331112"
        };

        private const int StartB = 104;
        private const int StartC = 105;
        private const int Stop = 106;

        public static List<bool> Encode(string data)
        {
            if (string.IsNullOrEmpty(data))
                throw new ArgumentException("Barcode data is empty.");

            bool allDigits = data.Length % 2 == 0;
            foreach (char c in data)
            {
                if (c < '0' || c > '9') { allDigits = false; break; }
            }

            var values = new List<int>();
            int start;
            if (allDigits)
            {
                start = StartC;
                for (int i = 0; i < data.Length; i += 2)
                    values.Add(int.Parse(data.Substring(i, 2)));
            }
            else
            {
                start = StartB;
                foreach (char c in data)
                {
                    if (c < 32 || c > 126) throw new ArgumentException("Unsupported character in barcode data.");
                    values.Add(c - 32);
                }
            }

            int sum = start;
            for (int i = 0; i < values.Count; i++)
                sum += values[i] * (i + 1);
            values.Add(sum % 103);

            var symbols = new List<int>();
            symbols.Add(start);
            symbols.AddRange(values);
            symbols.Add(Stop);

            var modules = new List<bool>();
            foreach (int s in symbols)
            {
                bool bar = true;
                foreach (char w in Patterns[s])
                {
                    int width = w - '0';
                    for (int k = 0; k < width; k++) modules.Add(bar);
                    bar = !bar;
                }
            }
            return modules;
        }
    }
}