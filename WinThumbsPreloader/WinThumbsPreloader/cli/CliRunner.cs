using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace WinThumbsPreloader.Cli
{
    class CliRunner
    {
        private static readonly TimeSpan InteractiveInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan LoggedInterval = TimeSpan.FromSeconds(5);
        private const int MaxReportedFailures = 20;
        private const int MaxReportedUnreadable = 20;

        //Windows ships thumbnail providers for these, so getting nothing back for one of
        //them usually means the file itself is damaged rather than the type being
        //unsupported. Types outside this set (svg, psd, camera raw, ...) legitimately
        //have no provider, which is a different thing entirely.
        private static readonly HashSet<string> ProviderBackedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff",
            ".webp", ".heic", ".heif", ".ico", ".mp4", ".m4v", ".mkv", ".avi", ".mov",
            ".wmv", ".webm", ".mpg", ".mpeg", ".3gp", ".mts", ".m2ts", ".asf", ".rmvb"
        };

        private CliOptions options;
        private ThumbnailPreloader.WTS_FLAGS extractFlags;

        private volatile bool cancelRequested;
        private bool cancelInProgress;
        private Exception fatalError;

        private Stopwatch stopwatch = new Stopwatch();
        private object progressLock = new object();
        private DateTime lastProgressWrite = DateTime.MinValue;
        private string currentItem = "";
        private bool progressLineOpen;

        private int totalProcessed;
        private int totalSucceeded;
        private int totalFailed;
        private int totalNoHandler;
        private int totalUnreadable;
        private int totalBadPaths;
        private int reportedFailures;
        private int reportedUnreadable;
        public CliRunner(CliOptions options)
        {
            this.options = options;
            if (options.cacheOnly)
            {
                extractFlags = ThumbnailPreloader.WTS_FLAGS.WTS_INCACHEONLY;
            }
            else if (options.forceExtraction)
            {
                extractFlags = ThumbnailPreloader.WTS_FLAGS.WTS_EXTRACTINPROC | ThumbnailPreloader.WTS_FLAGS.WTS_FORCEEXTRACTION;
            }
            else
            {
                extractFlags = ThumbnailPreloader.WTS_FLAGS.WTS_EXTRACTINPROC;
            }
        }

        public int Run()
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            try
            {
                for (int i = 0; i < options.paths.Count; i++)
                {
                    if (cancelRequested) break;
                    RunTarget(options.paths[i]);
                }
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
                CloseProgressLine();
            }
            return GetExitCode();
        }

        private void RunTarget(string target)
        {
            DirectoryScanner scanner = null;
            IEnumerable<string> items;
            IEnumerable<int> counts;

            if (Directory.Exists(target))
            {
                scanner = new DirectoryScanner(target, options.recursive, options.includeDirectories, options.followReparsePoints);
                items = scanner.GetItems();
                counts = scanner.GetItemsCount();
            }
            else if (File.Exists(target))
            {
                items = new string[] { target };
                counts = new int[] { 1 };
            }
            else
            {
                WriteErrorLine("Path not found: " + target);
                Interlocked.Increment(ref totalBadPaths);
                return;
            }

            long total = 0;
            if (!options.quiet)
            {
                foreach (int count in counts)
                {
                    if (cancelRequested) return;
                    total += count;
                    MaybeWriteProgress(0, total, "scanning", target, null);
                }
                CloseProgressLine();
                if (total > 0 && !options.verbose)
                {
                    WriteLine("Found " + FormatCount(total) + " item" + (total == 1 ? "" : "s") + " in " + target);
                }
            }

            if (scanner != null) scanner.ResetCounters();

            int processed = 0, succeeded = 0, failed = 0, unreadable = 0, noHandler = 0;
            stopwatch.Restart();

            if (options.dryRun)
            {
                foreach (string item in items)
                {
                    if (cancelRequested) break;
                    processed++;
                    if (options.verbose) WriteLine("would preload " + item);
                }
            }
            else if (options.jobs <= 1)
            {
                ThumbnailPreloader preloader = new ThumbnailPreloader(options.thumbnailSize);
                foreach (string item in items)
                {
                    if (cancelRequested) break;
                    int outcome = Process(preloader, item);
                    processed++;
                    if (outcome == 1) succeeded++;
                    else if (outcome == -1) unreadable++;
                    else if (outcome == -2) noHandler++;
                    else failed++;
                    ReportProgress(processed, total, target);
                }
            }
            else
            {
                ParallelTally tally = ProcessParallel(items, target, total);
                processed = tally.Processed;
                succeeded = tally.Succeeded;
                failed = tally.Failed;
                unreadable = tally.Unreadable;
                noHandler = tally.NoHandler;
            }

            stopwatch.Stop();
            CloseProgressLine();

            Interlocked.Add(ref totalProcessed, processed);
            Interlocked.Add(ref totalSucceeded, succeeded);
            Interlocked.Add(ref totalFailed, failed);
            Interlocked.Add(ref totalUnreadable, unreadable);
            Interlocked.Add(ref totalNoHandler, noHandler);

            if (!options.quiet && !options.verbose)
            {
                WriteLine(DescribeOutcome(target, processed, succeeded, failed, unreadable, noHandler));
            }
            if (scanner != null && scanner.unreadableDirectories > 0 && !options.quiet)
            {
                WriteLine("  note: " + scanner.unreadableDirectories + " folder(s) could not be read, last was \"" +
                    scanner.lastUnreadablePath + "\": " + scanner.lastUnreadableReason);
            }
        }

        //Returns 0 for a real failure, 1 for preloaded, -1 for a provider backed type that
        //produced nothing (usually a damaged file) and -2 for a type Windows cannot render.
        private int Process(ThumbnailPreloader preloader, string item)
        {
            currentItem = item;
            Exception error;
            ThumbnailPreloadResult result;
            try
            {
                result = preloader.Preload(item, extractFlags, out error);
            }
            catch (Exception e)
            {
                result = ThumbnailPreloadResult.Failed;
                error = e;
            }

            if (result == ThumbnailPreloadResult.Preloaded)
            {
                if (options.verbose) WriteLine("preloaded  " + item);
                return 1;
            }

            if (result == ThumbnailPreloadResult.NoThumbnail)
            {
                bool providerBacked = ProviderBackedExtensions.Contains(Path.GetExtension(item));
                if (options.verbose)
                {
                    WriteLine((providerBacked ? DescribeUnreadable() : "no handler ") + "  " + item);
                }
                else if (providerBacked)
                {
                    ReportUnreadable(item);
                }
                return providerBacked ? -1 : -2;
            }

            ReportFailure(item, error);
            return 0;
        }

        private string DescribeUnreadable()
        {
            return options.cacheOnly ? "not cached " : "unreadable ";
        }

        //A drive walk can fail on thousands of items, so only the first few are spelled out.
        private void ReportFailure(string item, Exception error)
        {
            int shown = Interlocked.Increment(ref reportedFailures);
            if (shown > MaxReportedFailures) return;
            WriteErrorLine("failed: " + item + "  (" + (error == null ? "unknown error" : error.Message) + ")");
            if (shown == MaxReportedFailures)
            {
                WriteErrorLine("... further failures are only counted, re-run with --verbose to list them.");
            }
        }

        //Damaged files are worth surfacing: the shell reports them the same way it reports
        //unsupported types, but a .jpg full of rubbish is a problem the user can act on.
        //In cache-only mode the same items are simply absent from the cache, not damaged.
        private void ReportUnreadable(string item)
        {
            int shown = Interlocked.Increment(ref reportedUnreadable);
            if (shown > MaxReportedUnreadable) return;
            WriteErrorLine((options.cacheOnly ? "not in cache: " : "no thumbnail, file looks damaged: ") + item);
            if (shown == MaxReportedUnreadable)
            {
                WriteErrorLine("... further items are only counted, re-run with --verbose to list them.");
            }
        }

        private class ParallelTally
        {
            public int Processed;
            public int Succeeded;
            public int Failed;
            public int Unreadable;
            public int NoHandler;
        }

        private ParallelTally ProcessParallel(IEnumerable<string> items, string target, long total)
        {
            ParallelTally result = new ParallelTally();
            BlockingCollection<string> queue = new BlockingCollection<string>(2048);
            int[] tally = new int[5];
            Exception workerError = null;
            List<Thread> workers = new List<Thread>();

            for (int i = 0; i < options.jobs; i++)
            {
                int workerNumber = i;
                Thread worker = new Thread(delegate()
                {
                    try
                    {
                        //One thumbnail cache per thread: COM objects are not free-threaded.
                        ThumbnailPreloader preloader = new ThumbnailPreloader(options.thumbnailSize);
                        foreach (string item in queue.GetConsumingEnumerable())
                        {
                            if (cancelRequested) break;
                            int outcome = Process(preloader, item);
                            int slot = (outcome == 1) ? 0 : (outcome == -1 ? 3 : (outcome == -2 ? 4 : 1));
                            Interlocked.Increment(ref tally[slot]);
                            ReportProgress(Interlocked.Increment(ref tally[2]), total, target);
                        }
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref workerError, e, null);
                    }
                });
                worker.IsBackground = true;
                worker.Name = "preload-" + workerNumber;
                workers.Add(worker);
                worker.Start();
            }

            foreach (string item in items)
            {
                if (cancelRequested) break;
                try
                {
                    queue.Add(item);
                }
                catch (InvalidOperationException)
                {
                    //Every consumer is gone, stop feeding.
                    break;
                }
            }
            queue.CompleteAdding();
            foreach (Thread worker in workers) worker.Join();

            if (workerError != null)
            {
                fatalError = workerError;
                WriteErrorLine("Worker thread failed: " + workerError.Message);
            }

            result.Succeeded = tally[0];
            result.Failed = tally[1];
            result.Processed = tally[2];
            result.Unreadable = tally[3];
            result.NoHandler = tally[4];
            return result;
        }

        private void ReportProgress(int processed, long total, string target)
        {
            MaybeWriteProgress(processed, total, "working", target, currentItem);
        }

        private void MaybeWriteProgress(int processed, long total, string phase, string target, string item)
        {
            if (options.quiet || options.verbose) return;

            if (Console.IsOutputRedirected)
            {
                lock (progressLock)
                {
                    if (lastProgressWrite != DateTime.MinValue && DateTime.UtcNow - lastProgressWrite < LoggedInterval) return;
                    lastProgressWrite = DateTime.UtcNow;
                }
                WriteLine(phase == "scanning"
                    ? "scanning  " + FormatCount(total) + " found  " + target
                    : "working  " + FormatCount(processed) + "/" + FormatCount(total) + "  " + target);
                return;
            }

            if (DateTime.UtcNow - lastProgressWrite < InteractiveInterval) return;

            StringBuilder line = new StringBuilder();
            line.Append(phase).Append(' ').Append(target).Append("  ");
            if (total > 0) line.Append('[').Append(((double)processed * 100.0 / total).ToString("00.0", CultureInfo.InvariantCulture)).Append(" %]");
            else line.Append("[  --  %]");
            line.Append("  ").Append(FormatCount(processed)).Append('/').Append(FormatCount(total));
            line.Append("   elapsed ").Append(((TimeSpan)stopwatch.Elapsed).ToString(@"hh\:mm\:ss"));
            if (item != null) line.Append("   ").Append(item);

            lock (progressLock)
            {
                int width = SafeWindowWidth();
                string text = line.ToString();
                if (text.Length > width) text = text.Substring(0, width);
                text = text.PadRight(width);
                if (progressLineOpen) Console.Out.Write("\r" + text);
                else Console.Out.Write(text);
                Console.Out.Flush();
                progressLineOpen = true;
                lastProgressWrite = DateTime.UtcNow;
            }
        }

        private static int SafeWindowWidth()
        {
            try
            {
                int width = Console.WindowWidth;
                if (width < 40) width = 80;
                if (width > 300) width = 300;
                return width - 1;
            }
            catch (Exception)
            {
                return 79;
            }
        }

        private void CloseProgressLine()
        {
            if (Console.IsOutputRedirected) return;
            lock (progressLock)
            {
                if (!progressLineOpen) return;
                Console.Out.Write("\r" + new String(' ', SafeWindowWidth()) + "\r");
                Console.Out.Flush();
                progressLineOpen = false;
            }
        }

        private static string FormatCount(long value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture);
        }

        private string DescribeOutcome(string target, int processed, int succeeded, int failed, int unreadable, int noHandler)
        {
            StringBuilder text = new StringBuilder();
            if (options.dryRun)
            {
                text.Append(target).Append(": would preload ").Append(FormatCount(processed)).Append(" item");
                if (processed != 1) text.Append('s');
                return text.ToString();
            }

            double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            text.Append(target).Append(": ").Append(FormatCount(processed)).Append(" processed, ")
                .Append(FormatCount(succeeded)).Append(" preloaded");
            if (options.cacheOnly)
            {
                int notCached = unreadable + noHandler;
                if (notCached > 0) text.Append(", ").Append(FormatCount(notCached)).Append(" not in cache");
            }
            else
            {
                if (unreadable > 0) text.Append(", ").Append(FormatCount(unreadable)).Append(" damaged or unreadable");
                if (noHandler > 0) text.Append(", ").Append(FormatCount(noHandler)).Append(" without a thumbnail handler");
            }
            text.Append(", ").Append(FormatCount(failed)).Append(" failed");
            text.Append(" in ").Append(((TimeSpan)stopwatch.Elapsed).ToString(@"hh\:mm\:ss"));
            text.Append(" (").Append(((double)processed / seconds).ToString("0", CultureInfo.InvariantCulture)).Append("/s)");
            return text.ToString();
        }

        private int GetExitCode()
        {
            if (cancelRequested) return CliProgram.ExitCanceled;
            if (totalBadPaths > 0) return CliProgram.ExitBadArguments;
            if (fatalError != null || totalFailed > 0) return CliProgram.ExitCompletedWithErrors;
            if (options.strict && (totalUnreadable > 0 || totalNoHandler > 0)) return CliProgram.ExitCompletedWithErrors;
            return CliProgram.ExitSuccess;
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            lock (progressLock)
            {
                if (cancelInProgress)
                {
                    //Second Ctrl+C, let the runtime tear us down.
                    e.Cancel = false;
                    return;
                }
                cancelInProgress = true;
            }
            e.Cancel = true;
            cancelRequested = true;
        }

        private static void WriteLine(string text)
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }

        private static void WriteErrorLine(string text)
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
        }
    }
}
