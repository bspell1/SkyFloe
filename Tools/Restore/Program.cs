﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Tpl = System.Threading.Tasks;

namespace SkyFloe.Restore
{
   class Program
   {
      private static String connectionString;
      private static String archiveName;
      private static String password;
      private static Dictionary<IO.Path, IO.Path> rootPathMap;
      private static IList<Regex> includeFilter;
      private static IList<Regex> excludeFilter;
      private static Boolean skipExisting;
      private static Boolean skipReadOnly;
      private static Boolean verifyResults;
      private static Boolean enableDeletes;
      private static List<IO.Path> restoreFiles;
      private static Int32 maxRetries;
      private static Int32 maxFailures;
      private static Int32 rateLimit;
      private static Int32 retries;
      private static Int32 failures;
      private static CancellationTokenSource canceler;

      static Int32 Main (String[] args)
      {
         Console.WriteLine("SkyFloe Restore");
         if (!ParseOptions(args))
         {
            ReportUsage();
            return 1;
         }
         if (!ExecuteRestore())
            return 1;
         return 0;
      }

      static Boolean ParseOptions (String[] args)
      {
         // initialize options
         password = "";
         rootPathMap = new Dictionary<IO.Path, IO.Path>();
         includeFilter = new List<Regex>();
         excludeFilter = new List<Regex>();
         maxRetries = 5;
         maxFailures = 5;
         restoreFiles = new List<IO.Path>();
         rateLimit = Int32.MaxValue / 1024;
         // parse options
         try
         {
            new Options.OptionSet()
            {
               { "c|connect=", v => connectionString = v },
               { "a|archive=", v => archiveName = v },
               { "p|password=", v => password = v },
               { "r|max-retry=", (Int32 v) => maxRetries = v },
               { "f|max-fail=", (Int32 v) => maxFailures = v },
               { "m|map-path=", v => rootPathMap.Add((IO.Path)v.Split('=')[0], (IO.Path)v.Split('=')[1]) },
               { "n|include=", v => includeFilter.Add(new Regex(v, RegexOptions.IgnoreCase)) },
               { "x|exclude=", v => excludeFilter.Add(new Regex(v, RegexOptions.IgnoreCase)) },
               { "e|skip-existing", v => skipExisting = (v != null) },
               { "o|skip-readonly", v => skipReadOnly = (v != null) },
               { "v|verify", v => verifyResults = (v != null) },
               { "k|delete", v => enableDeletes = (v != null) },
               { "i|file=", v => restoreFiles.Add((IO.Path)v) },
               { "l|rate=", (Int32 v) => rateLimit = v },
            }.Parse(args);
         }
         catch { return false; }
         // validate options
         if (String.IsNullOrWhiteSpace(connectionString))
            return false;
         if (String.IsNullOrWhiteSpace(archiveName))
            return false;
         if (maxRetries < 0)
            return false;
         if (maxFailures < 0)
            return false;
         if (rootPathMap.Any(p => String.IsNullOrWhiteSpace(p.Key)))
            return false;
         if (rootPathMap.Any(p => String.IsNullOrWhiteSpace(p.Value)))
            return false;
         if (restoreFiles.Any(f => String.IsNullOrWhiteSpace(f)))
            return false;
         if (rateLimit <= 0)
            return false;
         return true;
      }

      static void ReportUsage ()
      {
         Console.WriteLine("   Usage: SkyFloe-Restore {options}");
         Console.WriteLine("      -c|-connect {connect}      backup store connection string");
         Console.WriteLine("      -a|-archive {archive}      archive name");
         Console.WriteLine("      -p|-password {password}    archive password");
         Console.WriteLine("      -r|-max-retry {retries}    maximum file retries before skipping (default = 5)");
         Console.WriteLine("      -f|-max-fail {failures}    maximum file failures before aborting (default = 5)");
         Console.WriteLine("      -m|-map-path {src}={dst}   map a root backup path to a restore path");
         Console.WriteLine("      -n|-include {regex}        source path inclusion filter regular expression");
         Console.WriteLine("      -x|-exclude {regex}        source path exclusion filter regular expression");
         Console.WriteLine("      -e|-skip-existing[+/-]     ignore existing files (no overwrite)");
         Console.WriteLine("      -o|-skip-readonly[+/-]     ignore read-only files");
         Console.WriteLine("      -v|-verify[+/-]            verify CRCs of restored files");
         Console.WriteLine("      -k|-delete[+/-]            delete files marked as deleted in the archive");
         Console.WriteLine("      -i|-file {path}            specify an individual file to restore (source absolute path)");
         Console.WriteLine("      -l|-rate {limit}           restore rate limit, in KB/sec, default: unlimited");
      }

      static Boolean ExecuteRestore ()
      {
         var restoreOk = false;
         canceler = new CancellationTokenSource();
         if (Debugger.IsAttached)
            Console.SetBufferSize(1000, 1000);
         try
         {
            Console.Write("   Connecting to archive {0}...", archiveName);
            using (var engine = Connect())
            {
               Console.WriteLine("done.");
               var session = engine.Archive.Restores
                  .FirstOrDefault(s => s.State != SessionState.Completed);
               if (session != null)
                  Console.WriteLine(
                     "   Resuming restore session started {0:MMM d, yyyy h:m tt}.",
                     session.Created
                  );
               else
               {
                  Console.Write("   Creating a new restore session...");
                  var nodes = new List<Backup.Node>();
                  if (!restoreFiles.Any())
                     nodes.AddRange(engine.Archive.Roots);
                  else
                  {
                     foreach (var path in restoreFiles)
                     {
                        var node = engine.Archive.LookupNode(path);
                        if (node == null)
                           throw new InvalidOperationException(String.Format("Path not found in the archive: {0}.", path));
                        nodes.Add(node);
                     }
                  }
                  var subtrees = engine.Archive.GetSubtrees(nodes);
                  session = engine.CreateRestore(
                     new RestoreRequest()
                     {
                        RootPathMap = rootPathMap,
                        Filter = new RegexFilter()
                        {
                           Include = includeFilter,
                           Exclude = excludeFilter
                        },
                        SkipExisting = skipExisting,
                        SkipReadOnly = skipReadOnly,
                        VerifyResults = verifyResults,
                        EnableDeletes = enableDeletes,
                        RateLimit = rateLimit * 1024,
                        Entries = subtrees.Select(
                           n => engine.Archive.GetEntries(n)
                              .OrderBy(e => e.Session.Created)
                              .Where(
                                 e => e.State == Backup.EntryState.Completed ||
                                       e.State == Backup.EntryState.Deleted
                              ).Select(e => e.ID)
                              .DefaultIfEmpty(0)
                              .Last()
                        ).Where(id => id != 0),
                     }
                  );
                  Console.WriteLine("done.");
               }
               var tasks = Tpl.Task.Factory.StartNew(
                  () => engine.ExecuteRestore(session)
               );
               while (!tasks.Wait(1000))
               {
                  while (Console.KeyAvailable)
                  {
                     if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                     {
                        Console.WriteLine();
                        Console.Write("   Canceling...");
                        canceler.Cancel();
                     }
                  }
               }
            }
            Console.WriteLine("   Restore complete.");
            restoreOk = true;
         }
         catch (Exception e)
         {
            Console.WriteLine();
            if (canceler.IsCancellationRequested)
               Console.WriteLine("   Restore canceled.");
            else
               Console.WriteLine(
                  "   Restore failed: {0}",
                  e.ToString().Replace("\n", "\n      ")
               );
         }
         if (Debugger.IsAttached)
         {
            Console.WriteLine();
            Console.Write("Press enter to exit.");
            Console.ReadLine();
         }
         return restoreOk;
      }

      static Engine Connect ()
      {
         var engine = new Engine()
         {
            Connection = new Connection(connectionString),
            Canceler = canceler.Token
         };
         try
         {
            engine.OnProgress += HandleProgress;
            engine.OnError += HandleError;
            engine.OpenArchive(archiveName, password);
            return engine;
         }
         catch
         {
            engine.Dispose();
            throw;
         }
      }

      static void HandleProgress (Object sender, ProgressEventArgs args)
      {
         if (args.Operation == "BeginRestoreEntry")
         {
            Console.Write(
               "   {0:MM/dd hh:mm}: Total: {1}, Current: {2} - {3}...",
               DateTime.Now,
               FormatLength(args.RestoreSession.RestoreLength),
               FormatLength(args.RestoreEntry.Length),
               args.BackupEntry.Node.GetAbsolutePath()
            );
         }
         else if (args.Operation == "EndRestoreEntry")
         {
            Console.WriteLine("done.");
         }
         retries = failures = 0;
      }

      static void HandleError (Object sender, ErrorEventArgs args)
      {
         if (++retries <= maxRetries)
         {
            System.Threading.Thread.Sleep(retries * 1000);
            Console.WriteLine();
            Console.WriteLine("      Retrying...");
            args.Result = ErrorResult.Retry;
         }
         else if (++failures <= maxFailures && args.Action == "RestoreEntry")
         {
            retries = 0;
            Console.WriteLine();
            Console.WriteLine("      Skipping due to error.");
            Console.WriteLine(
               "         {0}",
               args.Exception.ToString().Replace("\n", "\n         ")
            );
            args.Result = ErrorResult.Fail;
         }
         else
            args.Result = ErrorResult.Abort;
      }

      static String FormatLength (Int64 bytes)
      {
         var units = new[] { "B", "KB", "MB", "GB", "TB" };
         var unit = 0;
         var norm = (Double)bytes;
         while (norm >= 1000 & unit < units.Length)
         {
            norm /= 1024;
            unit++;
         }
         var format = new StringBuilder();
         format.Append("{0:#,0");
         if (unit > 0 && norm < 10)
            format.Append(".00");
         else if (unit > 0 && norm < 100)
            format.Append(".0");
         format.Append(" ");
         format.Append(units[unit]);
         format.Append("}");
         return String.Format(format.ToString(), norm);
      }
   }
}
