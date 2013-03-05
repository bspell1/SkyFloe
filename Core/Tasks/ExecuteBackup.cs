﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Transactions;

namespace SkyFloe.Tasks
{
   public class ExecuteBackup : Task
   {
      private Store.IBackup backup;
      private IO.RateLimiter limiter;
      public SkyFloe.Backup.Session Session { get; set; }

      public override void Dispose ()
      {
         base.Dispose();
         if (this.backup != null)
            this.backup.Dispose();
         this.backup = null;
      }
      protected override void DoValidate ()
      {
         if (this.Session == null)
            throw new ArgumentException("Session");
         if (this.Session.State == Backup.SessionState.Completed)
            throw new ArgumentException("Session.State");
      }
      protected override void DoExecute ()
      {
         this.backup = this.Archive.PrepareBackup(this.Session);
         if (this.Session.State == SkyFloe.Backup.SessionState.Pending)
         {
            this.Session.State = SkyFloe.Backup.SessionState.InProgress;
            this.Archive.BackupIndex.UpdateSession(this.Session);
            Checkpoint();
         }
         try
         {
            this.limiter = new IO.RateLimiter(this.Session.RateLimit);
            var checkpointSize = 0L;
            for (; ; )
            {
               this.Canceler.ThrowIfCancellationRequested();
               SkyFloe.Backup.Entry entry = this.Archive.BackupIndex.LookupNextEntry(this.Session);
               if (entry == null)
                  break;
               BackupEntry(entry);
               checkpointSize += entry.Length;
               if (checkpointSize > this.Session.CheckpointLength)
               {
                  checkpointSize = 0;
                  Checkpoint();
               }
            }
            if (this.Archive.BackupIndex.LookupNextEntry(this.Session) == null)
            {
               this.Session.State = SkyFloe.Backup.SessionState.Completed;
               this.Archive.BackupIndex.UpdateSession(this.Session);
            }
            Checkpoint();
         }
         catch (OperationCanceledException)
         {
            Checkpoint();
            throw;
         }
      }
      private void BackupEntry (SkyFloe.Backup.Entry entry)
      {
         ReportProgress(
            new Engine.ProgressEventArgs()
            {
               Operation = "BeginBackupEntry",
               BackupSession = this.Session,
               BackupEntry = entry
            }
         );
         try
         {
            using (var input = new IO.StreamStack())
            {
               input.Push(IO.FileSystem.Open(entry.Node.GetAbsolutePath()));
               input.Push(new IO.CrcFilter(input.Top));
               if (this.Session.Compress)
                  input.Push(new IO.CompressionStream(input.Top, IO.CompressionMode.Compress));
               input.Push(new CryptoStream(input.Top, this.Crypto.CreateEncryptor(), CryptoStreamMode.Read));
               input.Push(this.limiter.CreateStreamFilter(input.Top));
               this.backup.Backup(entry, input);
               entry.Crc32 = input.GetStream<IO.CrcFilter>().Value;
            }
            entry.State = SkyFloe.Backup.EntryState.Completed;
            entry.Blob.Length += entry.Length;
            this.Session.ActualLength += entry.Length;
            using (var txn = new TransactionScope())
            {
               this.Archive.BackupIndex.UpdateEntry(entry);
               this.Archive.BackupIndex.UpdateBlob(entry.Blob);
               this.Archive.BackupIndex.UpdateSession(this.Session);
               txn.Complete();
            }
         }
         catch (Exception e)
         {
            switch (ReportError("BackupEntry", e))
            {
               case Engine.ErrorResult.Retry:
                  break;
               case Engine.ErrorResult.Fail:
                  entry = this.Archive.BackupIndex.FetchEntry(entry.ID);
                  entry.State = SkyFloe.Backup.EntryState.Failed;
                  this.Archive.BackupIndex.UpdateEntry(entry);
                  break;
               default:
                  throw;
            }
         }
         if (entry.State == SkyFloe.Backup.EntryState.Completed)
            ReportProgress(
               new Engine.ProgressEventArgs()
               {
                  Operation = "EndBackupEntry",
                  BackupSession = this.Session,
                  BackupEntry = entry
               }
            );
      }
      private void Checkpoint ()
      {
         ReportProgress(
            new Engine.ProgressEventArgs()
            {
               Operation = "BeginCheckpoint",
               BackupSession = this.Session
            }
         );
         WithRetry(
            "Checkpoint",
            () => this.backup.Checkpoint()
         );
         ReportProgress(
            new Engine.ProgressEventArgs()
            {
               Operation = "EndCheckpoint",
               BackupSession = this.Session
            }
         );
      }
   }
}
