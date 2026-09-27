using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinThumbsPreloader.Cli
{
    enum CliAction
    {
        Run,
        ShowHelp,
        ShowVersion
    }

    //What an option does. One value per supported option, dispatched in ApplyOption.
    enum OptionKind
    {
        ShowHelp,
        ShowVersion,
        Recursive,
        IncludeDirectories,
        ExcludeDirectories,
        FollowLinks,
        Force,
        CacheOnly,
        DryRun,
        Strict,
        Quiet,
        Verbose,
        ThumbnailSize,
        Jobs
    }

    //A single option and every spelling that selects it. All names must be lowercase.
    //takesValue marks the options that consume a value, either attached or as the next
    //argument; it is what lets a forward-slash group such as /s256 be recognised.
    sealed class OptionDefinition
    {
        public readonly string[] names;
        public readonly OptionKind kind;
        public readonly bool takesValue;

        public OptionDefinition(OptionKind kind, params string[] names)
            : this(kind, false, names)
        {
        }

        public OptionDefinition(OptionKind kind, bool takesValue, params string[] names)
        {
            this.kind = kind;
            this.takesValue = takesValue;
            this.names = names;
        }
    }

    //Command line parser for the console front-end.
    //Accepts "-r", "/r", "--recursive", grouped short flags ("-rq") and "--name=value".
    class CliOptions
    {
        public const string ProgramName = "WinThumbsPreloader-cli";

        public CliAction action = CliAction.Run;
        public string error = "";
        public List<string> paths = new List<string>();

        public bool recursive;
        public bool includeDirectories = true;
        public bool followReparsePoints;
        public bool forceExtraction;
        public bool cacheOnly;
        public bool dryRun;
        public bool strict;
        public bool quiet;
        public bool verbose;
        public uint thumbnailSize = ThumbnailPreloader.DefaultThumbnailSize;
        public int jobs = 1;

        public CliOptions(string[] arguments)
        {
            Parse(arguments);
        }

        private void Parse(string[] arguments)
        {
            bool endOfOptions = false;
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i];
                if (String.IsNullOrEmpty(argument)) continue;

                if (!endOfOptions && argument == "--")
                {
                    endOfOptions = true;
                    continue;
                }

                if (!endOfOptions && IsOptionToken(argument))
                {
                    if (!ParseOption(argument, arguments, ref i)) return;
                    continue;
                }

                paths.Add(argument);
            }

            if (action == CliAction.Run && paths.Count == 0 && error.Length == 0)
            {
                action = CliAction.ShowHelp;
            }
        }

        //A leading '-' is always an option. A leading '/' is only an option when it
        //names one, because /data/thumbs is a drive-relative path and must not be
        //split into grouped short options. UNC paths start with a backslash and drive
        //paths with a letter, so neither can be confused here.
        private static bool IsOptionToken(string argument)
        {
            if (argument.Length < 2) return false;
            char first = argument[0];
            if (first == '-') return true;
            if (first != '/') return false;
            return LooksLikeForwardSlashOption(argument.Substring(1));
        }

        private static bool LooksLikeForwardSlashOption(string body)
        {
            if (body.Length == 0) return false;

            int equals = body.IndexOf('=');
            if (equals >= 0) return IsKnownOptionName(body.Substring(0, equals));

            //A whole name such as help or version, which also covers single characters.
            if (IsKnownOptionName(body)) return true;

            //Either a group of flags such as /rq, or a legacy /s256 where the first
            //option swallows the rest as its value.
            if (!IsKnownShortOption(body[0])) return false;
            if (TakesValue(body[0])) return true;
            for (int i = 1; i < body.Length; i++)
            {
                if (!IsKnownShortOption(body[i])) return false;
            }
            return true;
        }

        private static bool IsKnownShortOption(char name)
        {
            return Find(char.ToLowerInvariant(name).ToString()) != null;
        }

        private static bool TakesValue(char name)
        {
            OptionDefinition definition = Find(char.ToLowerInvariant(name).ToString());
            return definition != null && definition.takesValue;
        }

        private static bool IsKnownOptionName(string name)
        {
            return Find(name.ToLowerInvariant()) != null;
        }

        //Every option the parser accepts, and the only place a new one is added. The name
        //lookups and the dispatch below both go through Find, so a name can never be
        //handled as an option while a forward-slash argument of the same name is treated
        //as a path.
        private static readonly OptionDefinition[] definitions =
        {
            new OptionDefinition(OptionKind.ShowHelp, "h", "help", "?"),
            new OptionDefinition(OptionKind.ShowVersion, "version"),
            new OptionDefinition(OptionKind.Recursive, "r", "recursive"),
            new OptionDefinition(OptionKind.IncludeDirectories, "directories", "dirs"),
            new OptionDefinition(OptionKind.ExcludeDirectories, "no-directories", "no-dirs", "files-only"),
            new OptionDefinition(OptionKind.FollowLinks, "follow-links"),
            new OptionDefinition(OptionKind.Force, "f", "force"),
            new OptionDefinition(OptionKind.CacheOnly, "cache-only", "cached"),
            new OptionDefinition(OptionKind.DryRun, "n", "dry-run"),
            new OptionDefinition(OptionKind.Strict, "strict"),
            new OptionDefinition(OptionKind.Quiet, "q", "quiet"),
            new OptionDefinition(OptionKind.Verbose, "v", "verbose"),
            new OptionDefinition(OptionKind.ThumbnailSize, true, "s", "size"),
            new OptionDefinition(OptionKind.Jobs, true, "j", "jobs")
        };

        private static OptionDefinition Find(string name)
        {
            for (int i = 0; i < definitions.Length; i++)
            {
                string[] names = definitions[i].names;
                for (int n = 0; n < names.Length; n++)
                {
                    if (String.Equals(names[n], name, StringComparison.Ordinal)) return definitions[i];
                }
            }
            return null;
        }

        private bool ParseOption(string argument, string[] arguments, ref int index)
        {
            string body = argument.Substring(1);
            bool isLong = (body.StartsWith("-", StringComparison.Ordinal));
            if (isLong) body = body.Substring(1);

            string inlineValue = null;
            int equals = body.IndexOf('=');
            if (equals >= 0)
            {
                inlineValue = body.Substring(equals + 1);
                body = body.Substring(0, equals);
            }

            if (body.Length == 0)
            {
                Fail("Unknown option '" + argument + "'.");
                return false;
            }

            //Grouped short options, e.g. "-rq" or legacy "-rs256". The last option in the
            //group may swallow the rest of the group or the next argument as its value.
            //A body that is a whole option name, such as /dirs, /dry-run or -cached, is
            //not a group and must reach ApplyOption intact.
            if (!isLong && inlineValue == null && body.Length > 1 && !IsKnownOptionName(body))
            {
                return ParseGroupedShortOptions(body, argument, arguments, ref index);
            }

            return ApplyOption(body, inlineValue, argument, arguments, ref index);
        }

        private bool ParseGroupedShortOptions(string group, string argument, string[] arguments, ref int index)
        {
            for (int i = 0; i < group.Length; i++)
            {
                string name = group.Substring(i, 1);
                string value = null;
                //Only an option that takes a value may swallow the rest of the group.
                //Handing it to a flag would let "-rq" stop at "-r" and discard "q", and
                //"-recurse" pass as "-r", so the remainder is left to the next flag.
                if (i + 1 < group.Length && TakesValue(name[0]))
                {
                    value = group.Substring(i + 1);
                    i = group.Length;
                }
                if (!ApplyOption(name, value, argument, arguments, ref index)) return false;
            }
            return true;
        }

        private bool ApplyOption(string rawName, string value, string argument, string[] arguments, ref int index)
        {
            OptionDefinition definition = Find(rawName.ToLowerInvariant());
            if (definition == null)
            {
                Fail("Unknown option '" + argument + "'.");
                return false;
            }

            switch (definition.kind)
            {
                case OptionKind.ShowHelp:
                    action = CliAction.ShowHelp;
                    return true;
                case OptionKind.ShowVersion:
                    action = CliAction.ShowVersion;
                    return true;
                case OptionKind.Recursive:
                    recursive = true;
                    return true;
                case OptionKind.IncludeDirectories:
                    includeDirectories = true;
                    return true;
                case OptionKind.ExcludeDirectories:
                    includeDirectories = false;
                    return true;
                case OptionKind.FollowLinks:
                    followReparsePoints = true;
                    return true;
                case OptionKind.Force:
                    forceExtraction = true;
                    return true;
                case OptionKind.CacheOnly:
                    cacheOnly = true;
                    return true;
                case OptionKind.DryRun:
                    dryRun = true;
                    return true;
                case OptionKind.Strict:
                    strict = true;
                    return true;
                case OptionKind.Quiet:
                    quiet = true;
                    return true;
                case OptionKind.Verbose:
                    verbose = true;
                    return true;

                case OptionKind.ThumbnailSize:
                {
                    uint size;
                    if (!TryReadValue(value, argument, arguments, ref index, out size)) return false;
                    if (size == 0 || size > 4096)
                    {
                        Fail("Invalid thumbnail size '" + size + "' (expected 1-4096).");
                        return false;
                    }
                    thumbnailSize = size;
                    return true;
                }

                case OptionKind.Jobs:
                {
                    int parsedJobs;
                    if (!TryReadValue(value, argument, arguments, ref index, out parsedJobs)) return false;
                    if (parsedJobs < 1 || parsedJobs > 64)
                    {
                        Fail("Invalid job count '" + parsedJobs + "' (expected 1-64).");
                        return false;
                    }
                    jobs = parsedJobs;
                    return true;
                }
            }

            //Reached only when a new OptionKind is added to the table above without a case
            //here, so that a forgotten option fails loudly instead of being ignored.
            Fail("Unknown option '" + argument + "'.");
            return false;
        }

        private bool TryReadValue(string value, string argument, string[] arguments, ref int index, out uint result)
        {
            result = 0;
            if (value == null)
            {
                if (index + 1 >= arguments.Length)
                {
                    Fail("Option '" + argument + "' requires a value.");
                    return false;
                }
                value = arguments[++index];
            }
            if (!UInt32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                Fail("Invalid value '" + value + "' for option '" + argument + "'.");
                return false;
            }
            return true;
        }

        private bool TryReadValue(string value, string argument, string[] arguments, ref int index, out int result)
        {
            result = 0;
            if (value == null)
            {
                if (index + 1 >= arguments.Length)
                {
                    Fail("Option '" + argument + "' requires a value.");
                    return false;
                }
                value = arguments[++index];
            }
            if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                Fail("Invalid value '" + value + "' for option '" + argument + "'.");
                return false;
            }
            return true;
        }

        private void Fail(string message)
        {
            if (error.Length == 0) error = message;
        }

        public static string HelpText
        {
            get
            {
                return
"Preload Windows Explorer thumbnails for files and folders from the console.\r\n" +
"\r\n" +
"Usage:\r\n" +
"    " + ProgramName + " [options] <path> [<path> ...]\r\n" +
"\r\n" +
"    Each <path> may be a folder (scanned) or a single file. Drive roots such as\r\n" +
"    X:\\ are valid targets.\r\n" +
"\r\n" +
"Options:\r\n" +
"    -r, --recursive      Descend into subfolders.\r\n" +
"    -s, --size <px>      Thumbnail size to request, 1-4096 (default " +
                    ThumbnailPreloader.DefaultThumbnailSize + ").\r\n" +
"    -f, --force          Re-extract items that already have a cached thumbnail.\r\n" +
"        --cache-only     Only read the existing thumbnail cache, extract nothing.\r\n" +
"        --files-only     Skip folder entries, preload files only.\r\n" +
"        --dirs           Include folder entries (default).\r\n" +
"        --follow-links   Descend into junctions and symlinks (off: avoids cycles).\r\n" +
"    -j, --jobs <n>       Extract with n worker threads, 1-64 (default 1).\r\n" +
"    -n, --dry-run        Count and list items, touch nothing.\r\n" +
"        --strict         Treat items without a thumbnail handler as failures.\r\n" +
"    -q, --quiet          No progress output.\r\n" +
"    -v, --verbose        One line per item.\r\n" +
"    -h, --help           Show this help.\r\n" +
"        --version        Show the version.\r\n" +
"\r\n" +
"    Short flags may be grouped (\"-rq\") and values may be attached (\"-s256\").\r\n" +
"    '--' ends option parsing. Press Ctrl+C to stop; press it twice to force quit.\r\n" +
"\r\n" +
"Notes:\r\n" +
"    Windows only preloads file types that have a thumbnail provider registered,\r\n" +
"    images and video for instance. Types without one (svg, psd, camera raw, ...)\r\n" +
"    are reported as having no handler, because Explorer draws those from the icon\r\n" +
"    cache instead. Files that do have a provider but produced no thumbnail are\r\n" +
"    counted as damaged or unreadable, which usually means the file itself is bad.\r\n" +
"\r\n" +
"Exit codes:\r\n" +
"    0  success\r\n" +
"    1  bad arguments or missing path\r\n" +
"    2  canceled with Ctrl+C\r\n" +
"    3  finished, but some items could not be preloaded\r\n" +
"\r\n" +
"Based on WinThumbsPreloader by Dmitry Bruhov (MIT).\r\n" +
"Project page: https://github.com/bruhov/WinThumbsPreloader\r\n";
            }
        }
    }
}
