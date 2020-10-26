using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Inedo.DependencyScan
{
    internal sealed class ArgList
    {
        public ArgList(string[] args)
        {
            var unnamed = args.Where(a => !a.StartsWith("-")).ToList();
            this.Command = unnamed.FirstOrDefault()?.ToLowerInvariant();
            this.Positional = unnamed.Skip(1).ToList().AsReadOnly();

            var regex = new Regex(@"^--?(?<1>[a-zA-Z0-9]+[a-zA-Z0-9\-]*)(=(?<2>.*))?$", RegexOptions.ExplicitCapture);
            var namedArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for(int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if(arg.StartsWith("-"))
                {
                    var match = regex.Match(arg);
                    if (!match.Success)
                        throw new PgScanException("Invalid argument: " + arg);

                    var name = match.Groups[1].Value;
                    if (namedArgs.ContainsKey(name))
                        throw new PgScanException($"Argument --{name} is specified more than once.");

                    var val = (match.Groups[2].Value ?? string.Empty).Trim('"', '\'');

                    namedArgs.Add(name, val);
                    _positions.Add(name, i);
                }
            }

            this.Named = namedArgs;
        }

        private Dictionary<string, int> _positions = new Dictionary<string, int>();

        public string Command { get; }
        public IReadOnlyList<string> Positional { get; }
        public IReadOnlyDictionary<string, string> Named { get; }

        public string TryGetPositional(int index) => index >= 0 && index < this.Positional.Count ? this.Positional[index] : null;

        public string GetRequiredNamed(string name)
        {
            if (this.Named.TryGetValue(name, out var value))
                return value;

            throw new PgScanException("Missing required argument --" + name);
        }

        public int GetNamedPosition(string name)
        {
            if (this._positions.TryGetValue(name, out var value))
                return value;

            return -1;
        }
    }
}
