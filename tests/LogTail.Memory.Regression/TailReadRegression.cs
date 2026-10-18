using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MyPowerTools.Runtime;

internal static class TailReadRegression
{
    internal static readonly string[] Cases =
    [
        "Physical-line exhaustive CR/LF equivalence",
        "Seeded arbitrary-byte and Unicode equivalence",
        "UTF-8/16/32 BOM and embedded BOM equivalence",
        "Block-edge CRLF and multibyte equivalence",
        "Short stream reads preserve physical lines",
        "Nonpositive limits read zero bytes",
        "Concurrent append is included before EOF",
        "Observed truncation during scan falls back safely",
        "Observed truncation after seek falls back safely",
        "Real-file truncation during scan and after seek",
        "Rotation retains one file handle",
        "Large single line is not truncated",
        "JSON parse and filtering outcomes match the reference"
    ];

    internal static int Run(string directory)
    {
        var checks = 0;
        var limits = new[] { int.MinValue, -1, 0, 1, 2, 3, 5, 10, 200, int.MaxValue };
        void Compare(byte[] bytes, string name, bool shortReads = false)
        {
            foreach (var limit in limits)
            {
                using var reference = new ObservedStream(new MemoryStream(bytes), shortReads ? 1 : int.MaxValue);
                var expected = FullScan(reference, limit);
                using var stream = new ObservedStream(new MemoryStream(bytes), shortReads ? 1 : int.MaxValue);
                var actual = LogRouter.ReadTailLines(stream, limit);
                if (!expected.SequenceEqual(actual))
                    throw new Exception($"{name}: line mismatch, limit={limit}, bytes={Convert.ToHexString(bytes.AsSpan(0, Math.Min(64, bytes.Length)))}");
                if (limit <= 0 && stream.BytesRead != 0)
                    throw new Exception("A nonpositive limit read file bytes.");
                checks++;
            }
        }

        // Exhaustive small line layouts include empty files, consecutive delimiters,
        // final delimiters, and every possible adjacent CR/LF combination.
        for (var size = 0; size <= 7; size++)
        {
            var combinations = (int)Math.Pow(3, size);
            for (var value = 0; value < combinations; value++)
            {
                var bytes = new byte[size];
                var remaining = value;
                for (var i = 0; i < size; i++, remaining /= 3)
                    bytes[i] = new byte[] { (byte)'a', (byte)'\r', (byte)'\n' }[remaining % 3];
                Compare(bytes, "exhaustive CR/LF");
            }
        }

        var random = new Random(0x5eed);
        for (var test = 0; test < 1200; test++)
        {
            var bytes = new byte[random.Next(0, 1000)];
            random.NextBytes(bytes);
            for (var i = 0; i < bytes.Length; i++)
                if (random.Next(9) == 0) bytes[i] = random.Next(2) == 0 ? (byte)'\r' : (byte)'\n';
            Compare(bytes, "seeded arbitrary bytes");
        }
        for (var test = 0; test < 250; test++)
        {
            var fragments = new[] { "行", "🙂", "é", "\uFEFF", "\u0085", "\u2028", "", " ", "null", "{}", "\r", "\n", "\r\n" };
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 100)).Select(_ => fragments[random.Next(fragments.Length)]));
            Compare(Encoding.UTF8.GetBytes(text), "seeded Unicode");
        }

        var encodings = new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true),
            new UnicodeEncoding(false, true), new UnicodeEncoding(true, true),
            new UTF32Encoding(false, true), new UTF32Encoding(true, true) };
        foreach (var encoding in encodings)
        {
            foreach (var text in new[] { "", "\r\n", "前🙂\r\n\uFEFFnext\n\nlast", "a\rb\n末尾\r\n", "null\n \n{}\r" })
            {
                var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
                Compare(bytes, encoding.WebName);
                Compare(bytes, encoding.WebName + " short reads", true);
            }
        }
        foreach (var prefix in new byte[][] { [0xef], [0xef, 0xbb], [0xff, 0xfe], [0xfe, 0xff], [0, 0, 0xfe], [0xff, 0xfe, 0] })
            Compare(prefix, "partial BOM");

        for (var delta = -5; delta <= 5; delta++)
        {
            foreach (var separator in new[] { "\r", "\n", "\r\n", "\n\r", "\r\r\n" })
            {
                var text = "old\n" + new string('x', 8192 + delta) + "🙂" + separator + "行\r\nlast🙂\n";
                Compare(Encoding.UTF8.GetBytes(text), "block edges");
            }
        }
        // Place a CRLF exactly across the backwards scanner's block boundary.
        Compare(Encoding.UTF8.GetBytes("old\r\n" + new string('x', 8191)), "split CRLF");
        Compare(Encoding.UTF8.GetBytes("old\r\n" + new string('x', 8190) + "\n"), "split CRLF with terminal newline");
        Compare(Encoding.UTF8.GetBytes("old\n" + new string('x', 2 * 1024 * 1024) + "🙂"), "unbounded single line");

        foreach (var (initial, addition) in new[] { ("old\none\ntwo\n", "three\nfour\n"), ("old\none\ntwo", "\nthree"), ("old\none\rtwo\r", "\nthree\r\n") })
        {
            using var stream = Mutable(initial);
            stream.BeforeRead = (_, readNumber) =>
            {
                if (readNumber == 3) Append(stream.Inner, Encoding.UTF8.GetBytes(addition));
            };
            var actual = LogRouter.ReadTailLines(stream, 2);
            using var reference = new MemoryStream(Encoding.UTF8.GetBytes(initial + addition));
            if (!actual.SequenceEqual(FullScan(reference, 2))) throw new Exception("Concurrent append mismatch.");
            checks++;
        }
        foreach (var truncateAtRead in new[] { 2, 3 })
        {
            using var stream = Mutable("history\nold\nlast\n");
            stream.BeforeRead = (_, readNumber) =>
            {
                if (readNumber == truncateAtRead) Replace(stream.Inner, Encoding.UTF8.GetBytes("new\n"));
            };
            if (!LogRouter.ReadTailLines(stream, 1).SequenceEqual(new[] { "new" }))
                throw new Exception($"Observed truncation mismatch at read {truncateAtRead}.");
            checks++;
        }

        foreach (var truncateAtRead in new[] { 2, 3 })
        {
            var truncationPath = Path.Combine(directory, "truncate.jsonl");
            File.WriteAllText(truncationPath, "history\nold\nlast\n");
            using var stream = new ObservedStream(new FileStream(truncationPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1));
            stream.BeforeRead = (_, readNumber) =>
            {
                if (readNumber == truncateAtRead) File.WriteAllText(truncationPath, "new\n");
            };
            if (!LogRouter.ReadTailLines(stream, 1).SequenceEqual(new[] { "new" }))
                throw new Exception($"Real-file truncation mismatch at read {truncateAtRead}.");
            checks++;
        }

        var rotationPath = Path.Combine(directory, "rotation.jsonl");
        File.WriteAllText(rotationPath, "old\none\ntwo\n");
        using (var file = new FileStream(rotationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var stream = new ObservedStream(file))
        {
            stream.BeforeRead = (_, readNumber) =>
            {
                if (readNumber != 2) return;
                File.Move(rotationPath, rotationPath + ".rotated");
                File.WriteAllText(rotationPath, "replacement\n");
            };
            if (!LogRouter.ReadTailLines(stream, 2).SequenceEqual(new[] { "one", "two" }))
                throw new Exception("Rotation mixed file handles.");
            checks++;
        }

        var router = new LogRouter(directory);
        var path = Path.Combine(directory, "fixture.jsonl");
        var record = new LogRecord(DateTimeOffset.UnixEpoch, "pkg", "fixture", "", 1, "", "info", "行🙂");
        var json = JsonSerializer.Serialize(record);
        foreach (var encoding in encodings)
        foreach (var body in new[] { $"bad JSON\n{json}\n", $"{json}\r\n \r\nnull\r\n", $"{json}\n\uFEFF{json}", $"{{\n{json}\n\n", $"{json}\rnull" })
        {
            File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(body)).ToArray());
            foreach (var limit in limits)
            {
                LogRecord[]? expected = null;
                Type? expectedException = null;
                try
                {
                    // File.ReadLines opens eagerly; TakeLast(nonpositive) never consumes
                    // or disposes it. Own the reference handle even for an empty result.
                    using var reference = File.OpenRead(path);
                    expected = Parse(FullScan(reference, limit));
                }
                catch (Exception error) { expectedException = error.GetType(); }
                LogRecord[]? actual = null;
                Type? actualException = null;
                try { actual = router.Tail("fixture", limit).ToArray(); }
                catch (Exception error) { actualException = error.GetType(); }
                if (expectedException != actualException || (expectedException is null && !expected!.SequenceEqual(actual!)))
                    throw new Exception($"JSON outcome mismatch for {encoding.WebName}, limit={limit}.");
                checks++;
            }
        }
        return checks;
    }

    internal static void Benchmark(string directory, string mode)
    {
        var path = Path.Combine(directory, "fixture.jsonl");
        using var stream = new ObservedStream(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: mode == "seek-tail" ? 1 : 4096));
        var timer = Stopwatch.StartNew();
        var lines = mode switch
        {
            "full-scan" => FullScan(stream, 200),
            "seek-tail" => LogRouter.ReadTailLines(stream, 200),
            _ => throw new ArgumentException("Expected full-scan or seek-tail benchmark mode.")
        };
        var rows = Parse(lines);
        timer.Stop();
        if (rows.Length != 200 || rows[0].EventSeq != 99800 || rows[^1].EventSeq != 99999)
            throw new Exception("Benchmark tail content mismatch.");
        Console.WriteLine(JsonSerializer.Serialize(new { mode, rows = rows.Length, first = rows[0].EventSeq,
            last = rows[^1].EventSeq, fileBytes = stream.Length, actualBytesRead = stream.BytesRead,
            readCalls = stream.ReadCalls, elapsedMs = timer.Elapsed.TotalMilliseconds,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
    }

    private static LogRecord[] Parse(IEnumerable<string> lines) => lines.Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<LogRecord>(line)).Where(record => record is not null).Cast<LogRecord>().ToArray();

    private static string[] FullScan(Stream stream, int limit)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Lines().TakeLast(limit).ToArray();
        IEnumerable<string> Lines()
        {
            while (reader.ReadLine() is { } line) yield return line;
        }
    }

    private static ObservedStream Mutable(string text)
    {
        var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Position = 0;
        return new ObservedStream(stream);
    }

    private static void Append(Stream stream, byte[] bytes)
    {
        var position = stream.Position;
        stream.Position = stream.Length;
        stream.Write(bytes);
        stream.Position = position;
    }

    private static void Replace(Stream stream, byte[] bytes)
    {
        var position = stream.Position;
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(bytes);
        stream.Position = position;
    }

    private sealed class ObservedStream(Stream inner, int maxRead = int.MaxValue) : Stream
    {
        internal Stream Inner => inner;
        internal long BytesRead { get; private set; }
        internal int ReadCalls { get; private set; }
        internal Action<ObservedStream, int>? BeforeRead { get; set; }
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            BeforeRead?.Invoke(this, ReadCalls);
            var count = inner.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
            BytesRead += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
