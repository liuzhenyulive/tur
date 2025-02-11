﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.FileSystemGlobbing;
using Tur.Extension;
using Tur.Option;
using Tur.Sink;
using Tur.Util;

namespace Tur.Handler;

public abstract class HandlerBase : IAsyncDisposable
{
    private readonly string _logFile;
    private readonly int _maxBytesScan;
    private readonly OptionBase _option;
    protected readonly ITurSink AggregateOutputSink;
    protected readonly CancellationToken CancellationToken;

    protected readonly ITurSink ConsoleSink;
    protected readonly ITurSink LogFileSink;

    protected HandlerBase(OptionBase option, CancellationToken cancellationToken)
    {
        _option = option;
        CancellationToken = cancellationToken;

        _logFile = Path.Combine(option.OutputDir,
            $"tur-{option.CmdName}-{GetRandomFile()}.log");

        ConsoleSink = new ConsoleSink(_option);
        LogFileSink = new FileSink(_logFile, option);
        AggregateOutputSink = new AggregateSink(ConsoleSink, LogFileSink);

        var gcMemoryInfo = GC.GetGCMemoryInfo();
        _maxBytesScan = Convert.ToInt32(Math.Min(gcMemoryInfo.TotalAvailableMemoryBytes / 10, 3 * 1024 * 1024));
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (CancellationToken.IsCancellationRequested)
        {
            await AggregateOutputSink.WarnLineAsync("  User requested to cancel ...");
        }

        await ConsoleSink.LightLineAsync($"{Constants.ArrowUnicode} Log file: {_logFile}");
        await AggregateOutputSink.DisposeAsync();
    }

    protected string GetRandomFile()
    {
        return Path.GetRandomFileName().Replace(".", string.Empty);
    }

    protected IEnumerable<string> EnumerateFiles(string dir, bool applyFilter = false, bool returnAbsolutePath = false)
    {
        if (!Directory.Exists(dir))
        {
            return Enumerable.Empty<string>();
        }

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
        if (applyFilter)
        {
            var matcher = new Matcher();
            if (_option.Includes is not {Length: > 0})
            {
                matcher.AddInclude("**");
            }
            else
            {
                foreach (var include in _option.Includes)
                {
                    matcher.AddInclude(include);
                }
            }

            if (_option.Excludes != null)
            {
                foreach (var exclude in _option.Excludes)
                {
                    matcher.AddExclude(exclude);
                }
            }
            
            var matchResult = matcher.Match(dir, files);
            if (returnAbsolutePath)
            {
                return matchResult.Files.Select(x => Path.Combine(dir, x.Path));
            }

            return matchResult.Files.Select(x =>
                Path.GetRelativePath(dir, Path.Combine(dir, x.Path))); // Matcher returns non cross platform separator
        }

        if (returnAbsolutePath)
        {
            return files;
        }

        return files.Select(x => Path.GetRelativePath(dir, x));
    }

    protected async Task CopyAsync(string srcFile, string destFile, Func<int, double, Task> progressChanged)
    {
        if (!File.Exists(srcFile))
        {
            return;
        }

        var destDir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        await using var src = new FileStream(srcFile, FileMode.Open, FileAccess.Read);
        var srcFileLength = src.Length;
        var buffer = new byte[Math.Min(_maxBytesScan, srcFileLength)];
        await using var dest = new FileStream(destFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        dest.SetLength(0);
        var bytesWritten = 0L;
        int currentBlockSize;

        var stopwatch = Stopwatch.StartNew();
        while ((currentBlockSize = await src.ReadAsync(buffer, 0, buffer.Length, CancellationToken)) > 0)
        {
            await dest.WriteAsync(buffer, 0, currentBlockSize, CancellationToken);
            stopwatch.Stop();
            bytesWritten += currentBlockSize;
            await progressChanged(Convert.ToInt32(bytesWritten / (double) srcFileLength * 100),
                (double) currentBlockSize * 2 / stopwatch.Elapsed.TotalSeconds);
            stopwatch.Restart();
        }
    }

    protected async Task<bool> IsSameFileAsync(string file1, string file2)
    {
        if (string.Equals(file1, file2))
        {
            return true;
        }

        var fileInfo1 = new FileInfo(file1);
        var fileInfo2 = new FileInfo(file2);
        if (fileInfo1.Length != fileInfo2.Length)
        {
            return false;
        }

        var maxBytesScan = Convert.ToInt32(Math.Min(_maxBytesScan, fileInfo1.Length));
        var iterations = (int) Math.Ceiling((double) fileInfo1.Length / maxBytesScan);
        await using var f1 = fileInfo1.OpenRead();
        await using var f2 = fileInfo2.OpenRead();
        var first = new byte[maxBytesScan];
        var second = new byte[maxBytesScan];

        for (var i = 0; i < iterations; i++)
        {
            if (CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var firstBytes = await f1.ReadAsync(first.AsMemory(0, maxBytesScan), CancellationToken);
            var secondBytes = await f2.ReadAsync(second.AsMemory(0, maxBytesScan), CancellationToken);
            if (firstBytes != secondBytes)
            {
                return false;
            }

            if (!AreBytesEqual(first, second))
            {
                return false;
            }
        }

        return true;
    }

    private bool AreBytesEqual(ReadOnlySpan<byte> b1, ReadOnlySpan<byte> b2)
    {
        return b1.SequenceEqual(b2);
    }

    protected IEnumerable<string> EnumerateDirectories(string dir, bool applyFilter = false,
        bool returnAbsolutePath = false)
    {
        if (!Directory.Exists(dir))
        {
            return Enumerable.Empty<string>();
        }

        var dirs = Directory
            .EnumerateDirectories(dir, "*", SearchOption.AllDirectories);
        if (applyFilter)
        {
            var matcher = new Matcher();
            if (_option.Includes is not {Length: > 0})
            {
                matcher.AddInclude("**");
            }
            else
            {
                foreach (var include in _option.Includes)
                {
                    matcher.AddInclude(include);
                }
            }

            if (_option.Excludes != null)
            {
                foreach (var exclude in _option.Excludes)
                {
                    matcher.AddExclude(exclude);
                }
            }

            var matchResult = matcher.Match(dir, dirs);
            if (returnAbsolutePath)
            {
                return matchResult.Files.Select(x => Path.Combine(dir, x.Path));
            }

            return matchResult.Files.Select(x => Path.GetRelativePath(dir, Path.Combine(dir, x.Path)));
        }

        if (returnAbsolutePath)
        {
            return dirs;
        }

        return dirs
            .Select(x => Path.GetRelativePath(dir, x));
    }

    public virtual async Task<int> HandleAsync()
    {
        await WriteLogFileHeaderAsync();
        var stopwatch = Stopwatch.StartNew();
        var exitCode = await HandleInternalAsync();
        stopwatch.Stop();
        await AggregateOutputSink.DefaultLineAsync(
            $"{Constants.ArrowUnicode} All done. Elapsed: [{stopwatch.Elapsed.Human()}]");
        return exitCode;
    }

    private async Task WriteLogFileHeaderAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Processed by tur {AppUtil.AppVersion}");
        sb.AppendLine($"### Command: tur {string.Join(" ", _option.RawArgs)}");
        sb.AppendLine();

        await File.WriteAllTextAsync(_logFile, sb.ToString(), new UTF8Encoding(false), CancellationToken)
            .OkForCancel();
    }

    protected abstract Task<int> HandleInternalAsync();
}