using System.Buffers;
using System.Text;

if (args.Length == 0)
{
    Console.WriteLine("Usage: bomcheck[.exe] <root_folder> [--autofix|-af]");
    Console.WriteLine("Examples: bomcheck.exe .\\repo -autofix");
    Console.WriteLine("          ./bomcheck ./repo");
    Console.WriteLine("Note: Only files with size under approx. 2GB (int32) are supported.");
    return;
}

var path = Path.GetFullPath(args[0]);
if (!Directory.Exists(path))
{
    Console.WriteLine($"Root folder {path} not found.");
    return;
}

Console.WriteLine($"Will check {path}...");

var autofix = args.Any(x =>
    x.Equals("--autofix", StringComparison.InvariantCultureIgnoreCase) ||
    x.Equals("-af", StringComparison.InvariantCultureIgnoreCase));

var enc = new UTF8Encoding(true);
var preamble = enc.GetPreamble();
var preambleLength = preamble.Length;

var bomFiles = 0;
var fixedFiles = 0;
var skippedFiles = 0;

var files = Directory.GetFiles(path, "*.*", new EnumerationOptions()
{
    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    IgnoreInaccessible = true,
    RecurseSubdirectories = true,
    ReturnSpecialDirectories = false
});

await Parallel.ForEachAsync(files, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
    async (file, ct) =>
    {
        await using var fs = File.OpenRead(file);
        if (fs.Length < preambleLength)
        {
            Console.WriteLine($"File {file} is too small to contain a BOM, skipping...");
            skippedFiles++;
            return;
        }

        var buf = ArrayPool<byte>.Shared.Rent(preambleLength);
        await fs.ReadAsync(buf.AsMemory(0, preambleLength), ct);
        var hasBom = buf[..preambleLength].SequenceEqual(preamble);
        ArrayPool<byte>.Shared.Return(buf);
        if (!hasBom) return;

        Console.WriteLine($"File {file} has a BOM");
        Interlocked.Increment(ref bomFiles);
        if (!autofix) return;

        if (fs.Length > int.MaxValue)
        {
            Console.WriteLine($"File {file} is too big to fix, skipping...");
            Interlocked.Increment(ref skippedFiles);
            return;
        }

        if (fs.Length == preambleLength)
        {
            Console.WriteLine($"File {file} has a BOM but is empty, skipping...");
            Interlocked.Increment(ref skippedFiles);
            return;
        }

        var contentLength = (int)fs.Length;
        var realLength = contentLength - preambleLength;
        var bytes = ArrayPool<byte>.Shared.Rent(realLength);
        fs.Seek(preambleLength, SeekOrigin.Begin);
        await fs.ReadAsync(bytes.AsMemory(0, realLength), ct);
        fs.Close();

        await File.WriteAllBytesAsync(file, bytes[..realLength], ct);
        ArrayPool<byte>.Shared.Return(bytes);
        Console.WriteLine($"File {file} fixed.");
        Interlocked.Increment(ref fixedFiles);
    });

Console.WriteLine($"Files with BOM: {bomFiles}");
Console.WriteLine($"Fixed files:    {fixedFiles}");
Console.WriteLine($"Skipped files:  {skippedFiles}");
Console.WriteLine($"Total files:    {files.Length}");
