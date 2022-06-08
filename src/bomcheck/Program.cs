using System.Buffers;
using System.Reflection;
using System.Text;

if (args.Length == 0)
{
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "'unknown'";
    Console.WriteLine($"{Assembly.GetExecutingAssembly().GetName().Name} version {version}");
    Console.WriteLine(
        "Usage: bomcheck[.exe] <root_folder> [--autofix|-af] [--skip-node-modules|-snm] [--fail-on-bom|-fob] [--fail-fast|-ff]");
    Console.WriteLine("Examples: bomcheck.exe .\\repo --autofix -snm");
    Console.WriteLine("          ./bomcheck ./repo");
    Console.WriteLine("Note: Only files with size under approx. 2GB (int32) are supported for automated fixing.");
    Environment.Exit(-1);
}

var path = Path.GetFullPath(args[0]);
if (!Directory.Exists(path))
{
    Console.WriteLine($"Root folder {path} not found.");
    Environment.Exit(-1);
}

Console.WriteLine($"Will check {path}...");

var autofix = args.Any(x =>
    x.Equals("--autofix", StringComparison.InvariantCultureIgnoreCase) ||
    x.Equals("-af", StringComparison.InvariantCultureIgnoreCase));

var failOnBom = args.Any(x =>
    x.Equals("--fail-on-bom", StringComparison.InvariantCultureIgnoreCase) ||
    x.Equals("-fob", StringComparison.InvariantCultureIgnoreCase));

var failFast = args.Any(x =>
    x.Equals("--fail-fast", StringComparison.InvariantCultureIgnoreCase) ||
    x.Equals("-ff", StringComparison.InvariantCultureIgnoreCase));

if (autofix && failOnBom)
{
    Console.WriteLine("--autofix and --fail-on-bom are mutually exclusive.");
    Environment.Exit(-1);
}

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
var toCheck = files.AsEnumerable();
if (args.Any(x => x.Equals("--skip-node-modules", StringComparison.InvariantCultureIgnoreCase) ||
                  x.Equals("-snm", StringComparison.InvariantCultureIgnoreCase)))
{
    toCheck = toCheck.Where(x => !x.Contains("node_modules"));
}

await Parallel.ForEachAsync(toCheck, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
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
        if (!autofix)
        {
            if (failFast && failOnBom) Environment.Exit(-1);
            return;
        }

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

if (failOnBom && bomFiles > 0)
{
    Console.WriteLine("Failed due to --fail-on-bom.");
    Environment.Exit(-1);
}
