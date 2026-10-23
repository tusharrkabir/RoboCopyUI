using System.Text.RegularExpressions;

namespace RoboCopyUI
{
    public static class RobocopyLineParser
    {
        public static readonly Regex ErrorRegex = new Regex(
            @"ERROR\s+(\d+)\s+\([^)]*\)\s+(.*)", RegexOptions.Compiled);

        private static readonly Regex RetryChatterRegex = new Regex(
            @"\s*(Waiting\s+\d+\s+seconds?\.\.\.\s*)?Retryin.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly string[] SkipTags = { "*EXTRA File", "*EXTRA Dir", "*Mismatch" };

        public static (bool isDirectory, long size, string path, string tag)? ParseFileLine(string line)
        {
            var trimmedLine = line.Trim();
            // Robocopy writes the per-file percentage after a carriage return
            // ("...C:\\file.bin\r100%").  Process output delivers that as one
            // record, so remove it before treating the final column as a path.
            trimmedLine = Regex.Replace(trimmedLine, @"\s*\d{1,3}(?:\.\d+)?%\s*$", string.Empty);
            if (string.IsNullOrEmpty(trimmedLine))
                return null;

            var rawTokens = Regex.Split(trimmedLine, @"\t+");
            var tokens = new System.Collections.Generic.List<string>();
            foreach (var raw in rawTokens)
            {
                var t = raw.Trim();
                if (!string.IsNullOrEmpty(t))
                    tokens.Add(t);
            }

            if (tokens.Count < 2)
                return null;

            var path = tokens[tokens.Count - 1];
            path = RetryChatterRegex.Replace(path, string.Empty).TrimEnd();

            var isDirectory = path.EndsWith("\\");

            long size = 0;
            string tag = string.Empty;
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                if (long.TryParse(tokens[i], out var parsedSize))
                    size = parsedSize;
                else
                    tag = tokens[i];
            }

            if (size == 0 && !isDirectory && string.IsNullOrEmpty(tag))
                return null;

            return (isDirectory, size, path, tag);
        }
    }
}
