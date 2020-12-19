﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader
{
    public class DownloadService : IDownloadService, IDisposable
    {
        public DownloadService(DownloadConfiguration options = null)
        {
            Package = new DownloadPackage()
            {
                Options = options ?? new DownloadConfiguration()
            };

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false; // accept the request for POST, PUT and PATCH verbs
            ServicePointManager.DefaultConnectionLimit = 1000;
            ServicePointManager.MaxServicePointIdleTime = 1000;
        }

        protected long TotalBytesReceived { get; set; }
        protected long LastTickCountCheckpoint { get; set; }
        protected const int OneSecond = 1000; // millisecond
        protected Request RequestInstance { get; set; }
        protected CancellationTokenSource GlobalCancellationTokenSource { get; set; }
        public bool IsBusy { get; protected set; }
        public long DownloadSpeed { get; protected set; }
        public DownloadPackage Package { get; set; }
        public event EventHandler<AsyncCompletedEventArgs> DownloadFileCompleted;
        public event EventHandler<DownloadProgressChangedEventArgs> DownloadProgressChanged;
        public event EventHandler<DownloadProgressChangedEventArgs> ChunkDownloadProgressChanged;
        
        public async Task DownloadFileAsync(DownloadPackage package)
        {
            Package = package;
            InitialBegin(package.Address.OriginalString);
            await StartDownload();
        }
        public async Task DownloadFileAsync(string address, DirectoryInfo folder)
        {
            InitialBegin(address);
            folder.Create();
            var filename = await RequestInstance.GetUrlDispositionFilenameAsync() ?? RequestInstance.GetFileName();
            Package.FileName = Path.Combine(folder.FullName, filename);

            await StartDownload();
        }
        public async Task DownloadFileAsync(string address, string fileName)
        {
            InitialBegin(address);
            Package.FileName = fileName;
            
            await StartDownload();
        }
        protected void InitialBegin(string address)
        {
            IsBusy = true;
            GlobalCancellationTokenSource = new CancellationTokenSource();
            RequestInstance = new Request(address, Package.Options.RequestConfiguration);
            Package.Address = RequestInstance.Address;
        }
        protected async Task StartDownload()
        {
            try
            {
                Package.TotalFileSize = await RequestInstance.GetFileSize();
                Validate();
                CheckSizes();

                if (File.Exists(Package.FileName))
                    File.Delete(Package.FileName);

                Package.Chunks = ChunkFile(Package.TotalFileSize, Package.Options.ChunkCount);
                var cancellationToken = GlobalCancellationTokenSource.Token;
                var tasks = new List<Task>();
                foreach (var chunk in Package.Chunks)
                {
                    if (Package.Options.ParallelDownload)
                    {
                        // download as parallel
                        var task = DownloadChunk(chunk, cancellationToken);
                        tasks.Add(task);
                    }
                    else
                    {
                        // download as async and serial
                        await DownloadChunk(chunk, cancellationToken);
                    }
                }

                if (Package.Options.ParallelDownload && cancellationToken.IsCancellationRequested == false
                ) // is parallel
                    Task.WaitAll(tasks.ToArray(), cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    OnDownloadFileCompleted(new AsyncCompletedEventArgs(null, true, Package));
                    return;
                }

                // Merge data to single file
                await MergeChunks(Package.Chunks);

                OnDownloadFileCompleted(new AsyncCompletedEventArgs(null, false, Package));
            }
            catch (OperationCanceledException exp)
            {
                OnDownloadFileCompleted(new AsyncCompletedEventArgs(exp, true, Package));
            }
            catch (Exception exp)
            {
                OnDownloadFileCompleted(new AsyncCompletedEventArgs(exp, false, Package));
                throw;
            }
            finally
            {
                if (GlobalCancellationTokenSource.Token.IsCancellationRequested == false)
                {
                    // remove temp files
                    ClearTemps();
                }
            }
        }
        protected void Validate()
        {
            var minNeededParts = (int)Math.Ceiling((double)Package.TotalFileSize / int.MaxValue); // for files as larger than 2GB
            Package.Options.ChunkCount = Package.Options.ChunkCount < minNeededParts ? minNeededParts : Package.Options.ChunkCount;
            Package.Options.Validate();
        }
        protected void CheckSizes()
        {
            if (Package.TotalFileSize <= 0)
                throw new InvalidDataException("File size is invalid!");

            CheckDiskSize(Package.FileName, Package.TotalFileSize);
            var areTempsStoredOnDisk = Package.Options.OnTheFlyDownload == false;
            if (areTempsStoredOnDisk)
            {
                var doubleFileSpaceNeeded = Directory.GetDirectoryRoot(Package.FileName) ==
                                            Directory.GetDirectoryRoot(Package.Options.TempDirectory);

                CheckDiskSize(Package.Options.TempDirectory, Package.TotalFileSize * (doubleFileSpaceNeeded ? 2 : 1));
            }
        }
        protected void CheckDiskSize(string directory, long actualSize)
        {
            var drive = new DriveInfo(Directory.GetDirectoryRoot(directory));
            if (drive.IsReady && actualSize >= drive.AvailableFreeSpace)
                throw new IOException($"There is not enough space on the disk `{drive.Name}`");
        }
        protected Chunk[] ChunkFile(long fileSize, int parts)
        {
            if (parts < 1)
                parts = 1;
            var chunkSize = fileSize / parts;

            if (chunkSize < 1)
            {
                chunkSize = 1;
                parts = (int)fileSize;
            }

            var chunks = new Chunk[parts];
            for (var i = 0; i < parts; i++)
            {
                var isLastChunk = i == parts - 1;
                var startPosition = i * chunkSize;
                var endPosition = isLastChunk ? fileSize - 1 : startPosition + chunkSize - 1;
                chunks[i] = new Chunk(startPosition, endPosition) {
                    MaxTryAgainOnFailover = Package.Options.MaxTryAgainOnFailover
                };
            }

            return chunks;
        }
        protected async Task<Chunk> DownloadChunk(Chunk chunk, CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested)
                    return chunk;

                var alreadyDownloaded = chunk.Start + chunk.Position >= chunk.End && chunk.Data?.LongLength == chunk.Length;
                if (alreadyDownloaded)
                    return chunk;

                var misDownload = chunk.Position >= chunk.Length && chunk.Data == null;
                if (misDownload)
                    chunk.Position = 0;

                var downloadRequest = RequestInstance.GetRequest();
                downloadRequest.AddRange(chunk.Start + chunk.Position, chunk.End);

                using var downloadResponse = downloadRequest.GetResponse() as HttpWebResponse;
                if (downloadResponse == null)
                    return chunk;

                using var responseStream = downloadResponse.GetResponseStream();
                if (responseStream == null)
                    return chunk;

                using var destinationStream = new ThrottledStream(responseStream, Package.Options.MaximumSpeedPerChunk);

                if (Package.Options.OnTheFlyDownload)
                    await ReadStreamOnTheFly(destinationStream, chunk, token);
                else
                    await ReadStreamOnTheFile(destinationStream, chunk, token);

                return chunk;
            }
            catch (TaskCanceledException) // when stream reader timeout occurred 
            {
                // re-request
                if (token.IsCancellationRequested == false)
                    await DownloadChunk(chunk, token);
            }
            catch (WebException) when (token.IsCancellationRequested == false && chunk.CanTryAgainOnFailover())
            {
                // when the host forcibly closed the connection.
                await Task.Delay(Package.Options.Timeout, token);
                chunk.Checkpoint();
                // re-request
                await DownloadChunk(chunk, token);
            }
            catch (Exception error) when (token.IsCancellationRequested == false && chunk.CanTryAgainOnFailover() &&
                                     (HasSource(error, "System.Net.Http") || HasSource(error, "System.Net.Sockets") ||
                                      HasSource(error, "System.Net.Security") || error.InnerException is SocketException))
            {
                // wait and decrease speed to low pressure on host
                Package.Options.Timeout += chunk.CanContinue() ? 0 : 200;
                chunk.Checkpoint();
                await Task.Delay(Package.Options.Timeout, token);
                // re-request
                await DownloadChunk(chunk, token);
            }
            catch (Exception e) // Maybe no internet!
            {
                OnDownloadFileCompleted(new AsyncCompletedEventArgs(e, false, Package));
                Debugger.Break();
            }

            return chunk;
        }
        protected async Task MergeChunks(Chunk[] chunks)
        {
            var directory = Path.GetDirectoryName(Package.FileName);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            if (Directory.Exists(directory) == false)
                Directory.CreateDirectory(directory);

            using var destinationStream = new FileStream(Package.FileName, FileMode.Append, FileAccess.Write);
            foreach (var chunk in chunks.OrderBy(c => c.Start))
            {
                if (Package.Options.OnTheFlyDownload)
                {
                    await destinationStream.WriteAsync(chunk.Data, 0, (int)chunk.Length);
                }
                else if (File.Exists(chunk.FileName))
                {
                    using var reader = File.OpenRead(chunk.FileName);
                    await reader.CopyToAsync(destinationStream);
                }
            }
        }
        protected void ClearTemps()
        {
            if (Package.Options.ClearPackageAfterDownloadCompleted && Package.Chunks != null)
            {
                Package.BytesReceived = 0;
                foreach (var chunk in Package.Chunks)
                {
                    if (Package.Options.OnTheFlyDownload)
                        chunk.Data = null;
                    else if (File.Exists(chunk.FileName))
                        File.Delete(chunk.FileName);

                    // reset position for download again
                    chunk.Position = 0;
                    chunk.FailoverCount = 0;
                    chunk.PositionCheckpoint = 0;
                    TotalBytesReceived = 0;
                }
                GC.Collect();
            }
        }
        protected bool HasSource(Exception exp, string source)
        {
            var e = exp;
            while (e != null)
            {
                if (string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase))
                    return true;

                e = e.InnerException;
            }

            return false;
        }
        protected async Task ReadStreamOnTheFly(Stream stream, Chunk chunk, CancellationToken token)
        {
            var bytesToReceiveCount = chunk.Length - chunk.Position;
            chunk.Data ??= new byte[chunk.Length];
            while (bytesToReceiveCount > 0)
            {
                if (token.IsCancellationRequested)
                    return;

                using var innerCts = new CancellationTokenSource(Package.Options.Timeout);
                var count = bytesToReceiveCount > Package.Options.BufferBlockSize
                    ? Package.Options.BufferBlockSize
                    : (int)bytesToReceiveCount;
                var readSize = await stream.ReadAsync(chunk.Data, chunk.Position, count, innerCts.Token);
                Package.BytesReceived += readSize;
                chunk.Position += readSize;
                bytesToReceiveCount = chunk.Length - chunk.Position;

                OnChunkDownloadProgressChanged(new DownloadProgressChangedEventArgs(chunk.Id, chunk.Length, chunk.Position, DownloadSpeed));
                OnDownloadProgressChanged(new DownloadProgressChangedEventArgs(null, Package.TotalFileSize, Package.BytesReceived, DownloadSpeed));
            }
        }
        protected async Task ReadStreamOnTheFile(Stream stream, Chunk chunk, CancellationToken token)
        {
            var bytesToReceiveCount = chunk.Length - chunk.Position;
            if (string.IsNullOrWhiteSpace(chunk.FileName) || File.Exists(chunk.FileName) == false)
                chunk.FileName = GetTempFile(Package.Options.TempDirectory, Package.Options.TempFilesExtension);

            using var writer = new FileStream(chunk.FileName, FileMode.Append, FileAccess.Write, FileShare.Delete);
            while (bytesToReceiveCount > 0)
            {
                if (token.IsCancellationRequested)
                    return;

                using var innerCts = new CancellationTokenSource(Package.Options.Timeout);
                var count = bytesToReceiveCount > Package.Options.BufferBlockSize
                    ? Package.Options.BufferBlockSize
                    : (int)bytesToReceiveCount;
                var buffer = new byte[count];
                var readSize = await stream.ReadAsync(buffer, 0, count, innerCts.Token);
                // ReSharper disable once MethodSupportsCancellation
                await writer.WriteAsync(buffer, 0, readSize);
                Package.BytesReceived += readSize;
                chunk.Position += readSize;
                bytesToReceiveCount = chunk.Length - chunk.Position;

                OnChunkDownloadProgressChanged(new DownloadProgressChangedEventArgs(chunk.Id, chunk.Length, chunk.Position, DownloadSpeed));
                OnDownloadProgressChanged(new DownloadProgressChangedEventArgs(null, Package.TotalFileSize, Package.BytesReceived, DownloadSpeed));
            }
        }
        protected string GetTempFile(string baseDirectory, string fileExtension = "")
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                return Path.GetTempFileName();

            if (!Directory.Exists(baseDirectory))
                Directory.CreateDirectory(baseDirectory);

            var filename = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N") + fileExtension);
            File.Create(filename).Close();

            return filename;
        }
        protected virtual void OnDownloadFileCompleted(AsyncCompletedEventArgs e)
        {
            IsBusy = false;
            DownloadFileCompleted?.Invoke(this, e);
        }
        protected virtual void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e)
        {
            OnDownloadSpeedCalculator();
            DownloadProgressChanged?.Invoke(this, e);
        }
        protected virtual void OnChunkDownloadProgressChanged(DownloadProgressChangedEventArgs e)
        {
            ChunkDownloadProgressChanged?.Invoke(this, e);
        }
        protected virtual void OnDownloadSpeedCalculator()
        {
            var duration = Environment.TickCount - LastTickCountCheckpoint + 1;
            if (duration < OneSecond)
                return;
            var newReceivedBytes = Package.BytesReceived - TotalBytesReceived;
            DownloadSpeed = newReceivedBytes * OneSecond / duration; // bytes per second
            LastTickCountCheckpoint = Environment.TickCount;
            TotalBytesReceived = Package.BytesReceived;
        }
        public void CancelAsync()
        {
            GlobalCancellationTokenSource?.Cancel(false);
        }
        public void Dispose()
        {
            Clear();
        }
        public void Clear()
        {
            GlobalCancellationTokenSource?.Dispose();
            GlobalCancellationTokenSource = new CancellationTokenSource();
            ClearTemps();

            Package.FileName = null;
            Package.TotalFileSize = 0;
            Package.BytesReceived = 0;
            Package.Chunks = null;
            RequestInstance = null;
            IsBusy = false;
        }
    }
}