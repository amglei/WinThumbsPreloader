using System;
using System.Reflection;
using System.Text;

namespace WinThumbsPreloader.Cli
{
    static class CliProgram
    {
        public const int ExitSuccess = 0;
        public const int ExitBadArguments = 1;
        public const int ExitCanceled = 2;
        public const int ExitCompletedWithErrors = 3;

        public const string DefaultVersion = "1.0.1";

        //Read from the assembly so a tagged build reports the version it was released
        //as. The fallback only applies if the file version metadata is missing.
        public static readonly string Version = ReadVersion();

        [STAThread]
        public static int Main(string[] arguments)
        {
            TryUseUtf8Console();

            CliOptions options = new CliOptions(arguments);

            if (options.error.Length > 0)
            {
                Console.Error.WriteLine(CliOptions.ProgramName + ": " + options.error);
                Console.Error.WriteLine("Try '" + CliOptions.ProgramName + " --help' for more information.");
                return ExitBadArguments;
            }

            switch (options.action)
            {
                case CliAction.ShowHelp:
                    Console.Out.Write(CliOptions.HelpText);
                    return ExitSuccess;
                case CliAction.ShowVersion:
                    Console.Out.WriteLine(CliOptions.ProgramName + " " + Version);
                    return ExitSuccess;
            }

            CliRunner runner = new CliRunner(options);
            return runner.Run();
        }

        private static string ReadVersion()
        {
            try
            {
                Assembly assembly = typeof(CliProgram).Assembly;

                AssemblyInformationalVersionAttribute informational = Attribute.GetCustomAttribute(
                    assembly, typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute;
                if (informational != null && !string.IsNullOrEmpty(informational.InformationalVersion))
                {
                    return informational.InformationalVersion;
                }

                AssemblyFileVersionAttribute file = Attribute.GetCustomAttribute(
                    assembly, typeof(AssemblyFileVersionAttribute)) as AssemblyFileVersionAttribute;
                if (file != null && !string.IsNullOrEmpty(file.Version))
                {
                    return file.Version;
                }
            }
            catch (Exception)
            {
                //Version metadata is optional, so fall through to the default.
            }
            return DefaultVersion;
        }

        //Item names are whatever the file system hands us, so make sure they survive
        //the trip to the terminal instead of turning into question marks.
        private static void TryUseUtf8Console()
        {
            try
            {
                if (Console.OutputEncoding.CodePage != Encoding.UTF8.CodePage)
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                }
            }
            catch (Exception)
            {
                //Redirected output or a legacy console - the default encoding will do.
            }
        }
    }
}
